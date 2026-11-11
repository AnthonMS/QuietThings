// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuietThings.Playback;

public sealed class TvNotificationAction {public string Action {get;set;} public string Title {get;set;}}
public sealed class TvNotification
{
 public const int MaxImageBytes=1048576;
 public string Id {get;set;} public string Title {get;set;} public string Message {get;set;}
 public int DurationSeconds {get;set;} public byte[] Image {get;set;} public string ImageExtension {get;set;}
 public bool ImageLive {get;set;}
 public List<TvNotificationAction> Actions {get;set;}=new();
 public DateTimeOffset Expires {get;set;}
 static bool Identifier(string value)=>value!=null&&Regex.IsMatch(value,@"\A[A-Za-z0-9_:-]{1,64}\z",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
 static string Text(JsonElement root,string name,int limit,bool optional=false) {
  if(optional&&!root.TryGetProperty(name,out _))return "";
  if(!root.TryGetProperty(name,out var field)||field.ValueKind!=JsonValueKind.String)throw new JsonException("Invalid notification text.");
  string value=field.GetString();if(value.Length>limit||value.Any(c=>char.IsControl(c)&&c!='\n'&&c!='\t'))throw new JsonException("Notification text is too long or invalid.");return value;
 }
 public static TvNotification Parse(JsonElement root,DateTimeOffset now,int defaultDuration=15) {
  if(root.ValueKind!=JsonValueKind.Object)throw new JsonException();
  var names=new HashSet<string>();foreach(var field in root.EnumerateObject())if(!names.Add(field.Name)||field.Name is not ("id" or "title" or "message" or "duration_seconds" or "image_base64" or "actions" or "image_live"))throw new JsonException("Unknown or duplicate notification field.");
  var value=new TvNotification{Id=root.TryGetProperty("id",out _)?Text(root,"id",64):Guid.NewGuid().ToString("N"),Title=Text(root,"title",100),Message=Text(root,"message",500),DurationSeconds=defaultDuration};
  if(!Identifier(value.Id)||string.IsNullOrWhiteSpace(value.Title))throw new JsonException();
  if(root.TryGetProperty("duration_seconds",out var duration)&&(duration.ValueKind!=JsonValueKind.Number||!duration.TryGetInt32(out int seconds)||seconds<5||seconds>120))throw new JsonException("Duration must be 5 to 120 seconds.");
  if(root.TryGetProperty("duration_seconds",out duration))value.DurationSeconds=duration.GetInt32();
  if(root.TryGetProperty("image_base64",out var image)) {
   if(image.ValueKind!=JsonValueKind.String||image.GetString().Length>((MaxImageBytes+2)/3)*4)throw new JsonException("Notification image is too large.");
   try{value.Image=Convert.FromBase64String(image.GetString());}catch(FormatException){throw new JsonException("Invalid notification image.");}
   value.ImageExtension=ImageType(value.Image);
  }
  if(root.TryGetProperty("image_live",out var live)){
   if(live.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new JsonException();value.ImageLive=live.GetBoolean();
   if(value.ImageLive&&(value.Image==null||value.ImageExtension==".gif"))throw new JsonException("Live camera needs a still-image frame.");
  }
  if(root.TryGetProperty("actions",out var actions)) {
   if(actions.ValueKind!=JsonValueKind.Array||actions.GetArrayLength()>3)throw new JsonException("At most three notification actions are supported.");
   var ids=new HashSet<string>();foreach(var action in actions.EnumerateArray()) {
    if(action.ValueKind!=JsonValueKind.Object||action.EnumerateObject().Count()!=2)throw new JsonException();
    string id=Text(action,"action",64),title=Text(action,"title",40);
    if(!Identifier(id)||id=="DISMISS"||!ids.Add(id)||string.IsNullOrWhiteSpace(title))throw new JsonException("Invalid or repeated notification action.");
    value.Actions.Add(new TvNotificationAction{Action=id,Title=title});
   }
  }
  value.Expires=now.AddSeconds(value.DurationSeconds);return value;
 }
 public static string ImageType(byte[] bytes) {
  if(bytes==null||bytes.Length<24||bytes.Length>MaxImageBytes)throw new JsonException("Image must be a bounded JPEG, PNG or GIF.");
  int width=0,height=0;string extension=null;
  int Big(int at)=>(bytes[at]<<8)|bytes[at+1];
  if(bytes.Take(6).SequenceEqual(new byte[]{71,73,70,56,57,97})||bytes.Take(6).SequenceEqual(new byte[]{71,73,70,56,55,97})) {
   width=bytes[6]|bytes[7]<<8;height=bytes[8]|bytes[9]<<8;extension=".gif";
   if(width>520||height>300)throw new JsonException("Resize animated images to at most 520 by 300 pixels.");
  }else if(bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})&&bytes[12]==73&&bytes[13]==72&&bytes[14]==68&&bytes[15]==82) {
   long w=((long)bytes[16]<<24)|((long)bytes[17]<<16)|((long)bytes[18]<<8)|bytes[19],h=((long)bytes[20]<<24)|((long)bytes[21]<<16)|((long)bytes[22]<<8)|bytes[23];
   if(w>int.MaxValue||h>int.MaxValue)throw new JsonException();width=(int)w;height=(int)h;extension=".png";
  }else if(bytes[0]==255&&bytes[1]==216){
   int at=2;
   while(at+3<bytes.Length){
    if(bytes[at++]!=255)throw new JsonException();while(at<bytes.Length&&bytes[at]==255)at++;if(at>=bytes.Length)break;
    int marker=bytes[at++];if(marker is 217 or 218)break;if(marker==1||(marker>=208&&marker<=215))continue;
    if(at+1>=bytes.Length)break;int size=Big(at);if(size<2||at+size>bytes.Length)throw new JsonException();
    if(marker>=192&&marker<=207&&marker is not (196 or 200 or 204)){if(size<7)throw new JsonException();height=Big(at+3);width=Big(at+5);extension=".jpg";break;}at+=size;
   }
  }
  if(extension==null||width<=0||height<=0||width>1600||height>900||(long)width*height>1440000||extension!=".gif"&&bytes.Length>131072)throw new JsonException("Resize the notification image to at most 1600 by 900 pixels and 128 KiB.");return extension;
 }
}

public sealed class TvNotificationImageUpdate
{
 public string Id {get;set;} public long Sequence {get;set;} public byte[] Image {get;set;} public string Extension {get;set;}
 public static TvNotificationImageUpdate Parse(JsonElement root) {
  if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count()!=3||!root.TryGetProperty("id",out var id)||id.ValueKind!=JsonValueKind.String||!Regex.IsMatch(id.GetString(),@"\A[A-Za-z0-9_:-]{1,64}\z",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))||!root.TryGetProperty("sequence",out var seq)||seq.ValueKind!=JsonValueKind.Number||!seq.TryGetInt64(out long number)||number<=0||!root.TryGetProperty("image_base64",out var data)||data.ValueKind!=JsonValueKind.String||data.GetString().Length>174764)throw new JsonException("Invalid live notification frame.");
  byte[] bytes;try{bytes=Convert.FromBase64String(data.GetString());}catch(FormatException){throw new JsonException();}
  string extension=TvNotification.ImageType(bytes);if(extension==".gif")throw new JsonException("Live frames must be JPEG or PNG.");
  return new TvNotificationImageUpdate{Id=id.GetString(),Sequence=number,Image=bytes,Extension=extension};
 }
}

public sealed class TvNotificationEvent
{
 public long Sequence {get;set;} public string Id {get;set;} public string NotificationId {get;set;} public string Action {get;set;}
 public DateTimeOffset Created {get;set;} public DateTimeOffset Expires {get;set;}
}
public sealed class TvNotificationEvents
{
 readonly object gate=new();readonly List<TvNotificationEvent> events=new();readonly Dictionary<string,DateTimeOffset> consumed=new();
 readonly string epoch=Guid.NewGuid().ToString("N");long sequence;
 public void DiscardPending(){lock(gate)events.Clear();}
 public bool Publish(TvNotification notification,string action,DateTimeOffset now) {
  lock(gate){Prune(now);if(notification==null||now>notification.Expires||consumed.ContainsKey(notification.Id)||!notification.Actions.Any(x=>x.Action==action))return false;
   consumed[notification.Id]=now.AddMinutes(5);events.Add(new TvNotificationEvent{Sequence=++sequence,Id=Guid.NewGuid().ToString("N"),NotificationId=notification.Id,Action=action,Created=now,Expires=now.AddSeconds(30)});while(events.Count>32)events.RemoveAt(0);return true;}
 }
 void Prune(DateTimeOffset now){events.RemoveAll(x=>x.Expires<now);foreach(string id in consumed.Where(x=>x.Value<now).Select(x=>x.Key).ToArray())consumed.Remove(id);}
 public object Read(long after,DateTimeOffset now){lock(gate){Prune(now);return new{ok=true,epoch,sequence,events=events.Where(x=>x.Sequence>after).Select(x=>new{sequence=x.Sequence,id=x.Id,notification_id=x.NotificationId,action=x.Action,created_at=x.Created.ToString("O"),expires_at=x.Expires.ToString("O")}).ToArray()};}}
}
