// SPDX-License-Identifier: MIT
// Copyright (c) 2026 QuietThings contributors

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Bearer-token QuietThings API hosted inside the launcher (power-free: inputs, volume, mute).
// Runs on its own background thread. Every failure is contained.
// Pure logic lives in LanApiCore.cs.
public sealed class LanApi : ILanBackend
{
 public const int Port=18765;
 const int MaxRequest=8192,IoTimeoutMs=3000,UiTimeoutMs=3000,TokenCheckMs=10000,MaxPerSecond=4;
 const string Listener="api-1";
 [DllImport("libvconf.so.0")] static extern int vconf_get_int(string key,out int value);
 [DllImport("libvconf.so.0")] static extern IntPtr vconf_get_str(string key);
 [DllImport("libc.so.6")] static extern void free(IntPtr p);
 [DllImport("libsource-api.so")] static extern IntPtr get_source_list();
 [DllImport("libcapi-media-sound-manager-tv.so.0")] static extern int sound_manager_get_master_volume(out int value);
 [DllImport("libcapi-media-sound-manager-tv.so.0")] static extern int sound_manager_get_master_mute(out int value);

 readonly SynchronizationContext ui;
 readonly Func<int,int> selectSource;   // runs on the UI thread only
 readonly Func<bool> isPaused;
 readonly Func<int,bool> setVolume;     // UI thread only
 readonly Func<bool,bool> setMute;      // UI thread only
 readonly Func<int,string> fallbackName; // inputs.json name for a port, or null
 readonly long startedAt=Environment.TickCount64;
 readonly LanRateLimiter limiter=new LanRateLimiter(MaxPerSecond);
 string token;
 DateTime tokenStamp=DateTime.MinValue;
 long tokenCheckedAt=long.MinValue;
 string tokenPath="";
 Thread thread;

 // Call from the UI thread (OnCreate) so SynchronizationContext.Current is the main loop.
 public LanApi(Func<int,int> selectSource,Func<bool> isPaused,Func<int,bool> setVolume,Func<bool,bool> setMute,Func<int,string> fallbackName)
 {
  this.selectSource=selectSource;this.isPaused=isPaused;this.setVolume=setVolume;this.setMute=setMute;this.fallbackName=fallbackName;
  ui=SynchronizationContext.Current;
  try{var d=Tizen.Applications.Application.Current.DirectoryInfo.Data;if(!string.IsNullOrEmpty(d))tokenPath=Path.Combine(d,"lan-api.token");}catch{}
  if(tokenPath=="")tokenPath="/tmp/quietthings-api.token";
 }

 public void Start()
 {
  try{thread=new Thread(Run){IsBackground=true,Name="quietthings-api"};thread.Start();}catch(Exception ex){Note("start failed: "+ex.GetType().Name);}
 }

 static string Describe(Exception ex)
 {
  var se=ex as SocketException;
  string m=(ex.Message??"").Replace('\r',' ').Replace('\n',' ');
  if(m.Length>200)m=m.Substring(0,200);
  return ex.GetType().Name+(se!=null?" code="+se.SocketErrorCode+" native="+se.NativeErrorCode:"")+" msg="+m;
 }

 // Never writes the token itself.
 void Note(string text){try{File.WriteAllText("/tmp/quietthings-api.txt",DateTime.UtcNow.ToString("o")+" "+Listener+" "+text+"\ntoken_file="+tokenPath+"\ntoken_present="+(token!=null)+"\n");}catch{}}

 void Run()
 {
  while(true){
   TcpListener l=null;
   try{
    l=new TcpListener(IPAddress.Any,Port);l.Start(2);
    LoadToken();Note("listening port="+Port);
    while(true){
     using(var c=l.AcceptTcpClient()){
      try{
       if(limiter.Allow(Environment.TickCount64))Handle(c);
       else{c.SendTimeout=IoTimeoutMs;Reply(c.GetStream(),429,LanApiCore.Fail(429,"rate limited").Json);}
      }catch{}
     }
    }
   }catch(Exception ex){Note("listener error: "+Describe(ex));}
   finally{try{l?.Stop();}catch{}}
   Thread.Sleep(2000);
  }
 }

 void LoadToken()
 {
  tokenCheckedAt=Environment.TickCount64;
  try{
   if(!File.Exists(tokenPath)){token=null;tokenStamp=DateTime.MinValue;return;}
   tokenStamp=File.GetLastWriteTimeUtc(tokenPath);
   var t=File.ReadAllText(tokenPath).Trim();
   token=t.Length>=16&&t.Length<=256?t:null;
  }catch{token=null;}
 }

 // Reload when the file changes (checked at most every 10 s); a missing token is retried on each request.
 void RefreshToken()
 {
  long now=Environment.TickCount64;
  if(token==null){LoadToken();return;}
  if(now-tokenCheckedAt<TokenCheckMs)return;
  tokenCheckedAt=now;
  try{
   if(!File.Exists(tokenPath)||File.GetLastWriteTimeUtc(tokenPath)!=tokenStamp)LoadToken();
  }catch{LoadToken();}
 }

 void Handle(TcpClient c)
 {
  c.ReceiveTimeout=IoTimeoutMs;c.SendTimeout=IoTimeoutMs;
  var s=c.GetStream();s.ReadTimeout=IoTimeoutMs;s.WriteTimeout=IoTimeoutMs;
  var buf=new byte[MaxRequest];int n=0,end=-1;
  var clock=System.Diagnostics.Stopwatch.StartNew();
  while(end<0){
   if(n==buf.Length){Reply(s,431,LanApiCore.Fail(431,"request too large").Json);return;}
   if(clock.ElapsedMilliseconds>IoTimeoutMs){Reply(s,408,LanApiCore.Fail(408,"timeout").Json);return;}
   int r=s.Read(buf,n,buf.Length-n);if(r<=0)return;n+=r;
   end=IndexOfHeaderEnd(buf,n);
  }
  LanRequest rq;LanResponse bad;
  if(!LanApiCore.TryParseHead(Encoding.ASCII.GetString(buf,0,end),out rq,out bad)){Reply(s,bad.Code,bad.Json);return;}
  // Read the body (up to the cap) so closing the socket does not reset the client's connection.
  byte[] body=null;
  int have=n-(end+4);
  if(rq.ContentLength<=LanApiCore.MaxBody){
   body=new byte[rq.ContentLength];
   int take=Math.Min(have,rq.ContentLength);if(take>0)Buffer.BlockCopy(buf,end+4,body,0,take);
   int got=take;
   while(got<rq.ContentLength){
    if(clock.ElapsedMilliseconds>IoTimeoutMs){Reply(s,408,LanApiCore.Fail(408,"timeout").Json);return;}
    int r=s.Read(body,got,rq.ContentLength-got);if(r<=0)return;got+=r;
   }
  }
  RefreshToken();
  var resp=LanApiCore.Handle(rq,token,body,this);
  Reply(s,resp.Code,resp.Json);
  if(rq.ContentLength>LanApiCore.MaxBody){
   // Best effort drain of an oversize body, bounded in bytes and time, so the 413 is not lost to a reset.
   try{long drained=have;while(drained<rq.ContentLength&&drained<65536&&clock.ElapsedMilliseconds<IoTimeoutMs){int r=s.Read(buf,0,buf.Length);if(r<=0)break;drained+=r;}}catch{}
  }
 }

 static int IndexOfHeaderEnd(byte[] b,int n)
 {
  for(int i=3;i<n;i++)if(b[i-3]==13&&b[i-2]==10&&b[i-1]==13&&b[i]==10)return i-3;
  return -1;
 }

 static void Reply(NetworkStream s,int code,string json)
 {
  var all=LanApiCore.FormatHttp(code,json);
  s.Write(all,0,all.Length);s.Flush();
 }

 // ---------- ILanBackend ----------

 public bool Paused{get{return isPaused();}}
 public long UptimeSeconds{get{return (Environment.TickCount64-startedAt)/1000;}}
 public void Delay(int ms){Thread.Sleep(ms);}

 // vconf is a plain thread-safe key store; read it directly on the listener thread.
 // Unverified on the TV: whether this app process may read memory/eden/source/current_source.
 // If that read fails, the selected port falls back to memory/system/source_type.
 public LanSelection ReadSelection()
 {
  int st=-1;
  try{int n;st=vconf_get_int("memory/system/source_type",out n)==0?n:-1;}catch{st=-1;}
  string cur=null;
  try{
   IntPtr p=vconf_get_str("memory/eden/source/current_source");
   if(p!=IntPtr.Zero){
    try{cur=Marshal.PtrToStringAnsi(p);}catch{cur=null;}
    try{free(p);}catch{}
   }
  }catch{cur=null;}
  return LanApiCore.MakeSelection(cur,st);
 }

 // Anything touching Samsung's services runs on the UI thread via the captured
 // SynchronizationContext (TizenSynchronizationContext posts to the main loop).
 T OnUi<T>(Func<T> f)
 {
  if(ui==null)throw new InvalidOperationException("no ui context");
  T result=default(T);Exception error=null;
  // Never disposed: a late callback may still Set() it after a timeout, and nothing
  // may escape the posted callback into the UI main loop.
  var done=new ManualResetEventSlim(false);
  ui.Post(_=>{
   try{try{result=f();}catch(Exception e){error=e;}}
   catch{}
   try{done.Set();}catch{}
  },null);
  if(!done.Wait(UiTimeoutMs))throw new TimeoutException("ui thread did not respond");
  if(error!=null)throw error;
  return result;
 }

 // UI thread only. get_source_list() returns a fresh malloc'd copy (strdup of the source JSON) on every
 // call, so the caller owns it and must free() it once: measured on this TV, 3000 calls without free
 // returned 3000 different addresses and grew RSS by about 9 MB, while with free the address and RSS stayed
 // flat and nothing crashed. Copy first, then free exactly once; a failure in either step is contained.
 // Because nothing is leaked, the list is read fresh on every call and not cached.
 // Also called by the launcher itself (UI thread) so its cards use the same Samsung names.
 public static List<LanSource> ReadSources()
 {
  IntPtr p=IntPtr.Zero;
  try{
   p=get_source_list();
   string raw=p==IntPtr.Zero?null:Marshal.PtrToStringAnsi(p);
   return raw!=null?LanApiCore.ParseSourceList(raw):null;
  }catch{return null;}
  finally{
   if(p!=IntPtr.Zero){try{free(p);}catch{}}
  }
 }

 public LanNative ReadNative(int selectedPort)
 {
  return OnUi(()=>{
   var r=new LanNative();
   r.Inputs=LanApiCore.WithFallbackNames(ReadSources(),fallbackName);
   try{int v;r.Volume=sound_manager_get_master_volume(out v)==0?v:-1;}catch{r.Volume=-1;}
   try{int m;r.Mute=sound_manager_get_master_mute(out m)==0?m:-1;}catch{r.Mute=-1;}
   return r;
  });
 }

 public int SelectPort(int port){int want=12+port;return OnUi(()=>selectSource(want));}
 public bool SetVolume(int level){return OnUi(()=>setVolume(level));}
 public bool SetMute(bool mute){return OnUi(()=>setMute(mute));}
}
