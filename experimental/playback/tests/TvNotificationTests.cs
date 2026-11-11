using QuietThings.Playback;
using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
class Tests {
 static int checks;static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
 static TvNotification Parse(string json,DateTimeOffset now){using var doc=JsonDocument.Parse(json);return TvNotification.Parse(doc.RootElement,now);}
 static void Reject(string json,DateTimeOffset now){try{Parse(json,now);}catch(JsonException){checks++;return;}throw new Exception("Expected notification rejection");}
 static async Task Main(){
  var now=DateTimeOffset.UtcNow;string valid="{\"id\":\"doorbell-1\",\"title\":\"Front door\",\"message\":\"Someone rang the bell\",\"actions\":[{\"action\":\"UNLOCK_TEST\",\"title\":\"Test action\"}]}";
  var message=Parse(valid,now);Check(message.DurationSeconds==15&&message.Actions.Count==1&&message.Expires==now.AddSeconds(15),"bounded defaults and action parsed");
  foreach(string json in new[]{"{}","[]",valid.Replace("doorbell-1","../outside"),valid.Replace("UNLOCK_TEST","../bad"),valid.Replace("UNLOCK_TEST","DISMISS"),valid.Replace("\"title\":\"Front door\"","\"title\":\"Front door\",\"title\":\"duplicate\""),valid.Replace("\"id\":","\"unknown\":true,\"id\":"),valid.Replace("\"actions\":","\"duration_seconds\":0,\"actions\":"),valid.Replace("\"actions\":","\"duration_seconds\":121,\"actions\":"),valid.Replace("\"actions\":","\"duration_seconds\":\"15\",\"actions\":"),valid.Replace("Front door",new string('x',101))})Reject(json,now);
  var events=new TvNotificationEvents();Check(!events.Publish(message,"UNKNOWN",now),"unlisted action cannot be forged");Check(events.Publish(message,"UNLOCK_TEST",now),"explicit current action accepted");Check(!events.Publish(message,"UNLOCK_TEST",now),"double press cannot repeat action");
  using(var data=JsonDocument.Parse(JsonSerializer.Serialize(events.Read(0,now)))){var e=data.RootElement.GetProperty("events");Check(e.GetArrayLength()==1&&e[0].GetProperty("action").GetString()=="UNLOCK_TEST","event contains chosen action");Check(data.RootElement.GetProperty("epoch").GetString().Length==32,"event epoch identifies launcher lifetime");}
  using(var data=JsonDocument.Parse(JsonSerializer.Serialize(events.Read(1,now))))Check(data.RootElement.GetProperty("events").GetArrayLength()==0,"event cursor avoids repeated delivery");
  using(var data=JsonDocument.Parse(JsonSerializer.Serialize(events.Read(0,now.AddSeconds(31)))))Check(data.RootElement.GetProperty("events").GetArrayLength()==0,"unconsumed actions expire quickly");
  var queued=Parse(valid.Replace("doorbell-1","queued"),now);Check(events.Publish(queued,"UNLOCK_TEST",now),"fresh action queued before access disabled");
  events.DiscardPending();using(var data=JsonDocument.Parse(JsonSerializer.Serialize(events.Read(0,now))))Check(data.RootElement.GetProperty("events").GetArrayLength()==0&&data.RootElement.GetProperty("sequence").GetInt64()==2,"disabling notification access discards pending actions without replaying cursor");
  var expired=Parse(valid.Replace("doorbell-1","expired"),now);Check(!events.Publish(expired,"UNLOCK_TEST",now.AddSeconds(16)),"expired notification cannot execute action");
  byte[] png=new byte[4096];new byte[]{137,80,78,71,13,10,26,10}.CopyTo(png,0);new byte[]{73,72,68,82}.CopyTo(png,12);png[18]=2;png[19]=128;png[22]=1;png[23]=104;
  Check(TvNotification.ImageType(png)==".png","inline PNG dimensions accepted");png[16]=127;try{TvNotification.ImageType(png);throw new Exception("Oversize image accepted");}catch(JsonException){checks++;}png[16]=0;
  string image=JsonSerializer.Serialize(new{title="Camera test",message="Test image",image_base64=Convert.ToBase64String(png)});Check(Parse(image,now).Image.Length==4096,"bounded inline image parsed");
  byte[] gif=new byte[32];System.Text.Encoding.ASCII.GetBytes("GIF89a").CopyTo(gif,0);gif[6]=200;gif[8]=120;
  Check(TvNotification.ImageType(gif)==".gif","bounded animated GIF header accepted");gif[7]=4;try{TvNotification.ImageType(gif);throw new Exception("Oversize GIF accepted");}catch(JsonException){checks++;}gif[7]=0;
  Reject(valid.Replace("\"actions\":","\"image_live\":true,\"actions\":"),now);
  string frameJson=JsonSerializer.Serialize(new{id="camera-1",sequence=1,image_base64=Convert.ToBase64String(png)});
  Check(TvNotificationImageUpdate.Parse(JsonDocument.Parse(frameJson).RootElement).Sequence==1,"live frame sequence parsed");
  foreach(string invalid in new[]{frameJson.Replace("camera-1","../bad"),frameJson.Replace("\"sequence\":1","\"sequence\":0"),frameJson.Replace("\"sequence\":1","\"sequence\":\"1\""),frameJson.Replace("\"sequence\":1","\"sequence\":1,\"extra\":true")}){
   try{TvNotificationImageUpdate.Parse(JsonDocument.Parse(invalid).RootElement);throw new Exception("Bad live frame accepted");}catch(JsonException){checks++;}
  }
  int imageReceived=0;
  int received=0;bool enabled=true;const string token="notification-test-token-12345678";
  using var agent=new PlaybackAgent(ct=>Task.FromResult("com.samsung.tv.bixby-promotion"),()=>null,(_,ct)=>Task.FromResult(IPAddress.Loopback));
  using var server=new PlaybackServer(agent,()=>token,0,IPAddress.Loopback,notifications:(root,ct)=>{var n=TvNotification.Parse(root,now);received++;return Task.FromResult<object>(new{ok=true,id=n.Id});},accessEnabled:()=>enabled,notificationEvents:(after,ct)=>Task.FromResult(events.Read(after,now)),notificationImage:(root,ct)=>{var frame=TvNotificationImageUpdate.Parse(root);imageReceived++;return Task.FromResult<object>(new{ok=true,updated=true});},notificationStatus:ct=>Task.FromResult<object>(new{ok=true,active=true}));
  server.Start();using var client=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+server.Port)};client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
  using(var response=await client.PostAsync("/v1/notifications",new StringContent(image)))Check(response.StatusCode==HttpStatusCode.OK&&received==1,"notification route accepts image above old 1KB command limit");
  using(var response=await client.PostAsync("/v1/command",new StringContent(image)))Check(response.StatusCode==HttpStatusCode.RequestEntityTooLarge,"command limit remains 1KB");
  using(var response=await client.GetAsync("/v1/notifications/events?after=-1"))Check(response.StatusCode==HttpStatusCode.BadRequest,"negative event cursor rejected");
  using(var response=await client.PostAsync("/v1/notifications/image",new StringContent(frameJson)))Check(response.StatusCode==HttpStatusCode.OK&&imageReceived==1,"live frame endpoint accepts authenticated bounded image");
  using(var response=await client.GetAsync("/v1/notifications/status"))Check(response.StatusCode==HttpStatusCode.OK,"notification status endpoint available");
  enabled=false;using(var response=await client.GetAsync("/v1/playback"))Check(response.StatusCode==HttpStatusCode.ServiceUnavailable,"disabled access is temporary unavailability, not bad token");
  client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","wrong-token");using(var response=await client.PostAsync("/v1/notifications",new StringContent(valid)))Check(response.StatusCode==HttpStatusCode.Unauthorized&&received==1,"authentication precedes disabled state and notification callback");
  Console.WriteLine(checks+" notification validation, single-action, expiry, image and real HTTP checks passed.");
 }
}
