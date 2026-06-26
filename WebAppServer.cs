using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace HonHidVerifier
{
    internal sealed class WebAppServer : IDisposable
    {
        private readonly ScannerService _scanner = new ScannerService();
        private readonly HttpListener _listener = new HttpListener();
        private Thread _thread;
        private volatile bool _running;

        public string Url { get; private set; }

        public void Start()
        {
            Url = "http://127.0.0.1:8765/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _running = true;
            _thread = new Thread(ServerLoop) { IsBackground = true, Name = "Local web app" };
            _thread.Start();
        }

        public void OpenBrowser()
        {
            Process.Start(Url);
        }

        private void ServerLoop()
        {
            while (_running)
            {
                try
                {
                    HttpListenerContext context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(delegate { Handle(context); });
                }
                catch
                {
                    if (_running)
                        Thread.Sleep(200);
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url.AbsolutePath.ToLowerInvariant();
                if (path == "/")
                    WriteText(context, LoadHtml(), "text/html; charset=utf-8");
                else if (path == "/api/state")
                    WriteJson(context, StateJson(_scanner.GetState()));
                else if (path == "/api/detect")
                    WriteJson(context, StateJson(_scanner.DetectAndConnect()));
                else if (path == "/api/settings")
                    WriteJson(context, ResultJson(_scanner.SendSettingsCommand()));
                else if (path == "/api/send")
                    WriteJson(context, ResultJson(_scanner.SendCommand(ReadCommand(context))));
                else
                {
                    context.Response.StatusCode = 404;
                    WriteText(context, "Not found", "text/plain; charset=utf-8");
                }
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                WriteJson(context, "{\"success\":false,\"response\":\"" + Json(ex.Message) + "\"}");
            }
        }

        private static string ReadCommand(HttpListenerContext context)
        {
            using (var reader = new StreamReader(context.Request.InputStream,
                context.Request.ContentEncoding ?? Encoding.UTF8))
            {
                string body = reader.ReadToEnd();
                if (body.StartsWith("command=", StringComparison.Ordinal))
                    return Uri.UnescapeDataString(body.Substring("command=".Length).Replace("+", " "));
                return body;
            }
        }

        private static void WriteJson(HttpListenerContext context, string json)
        {
            WriteText(context, json, "application/json; charset=utf-8");
        }

        private static void WriteText(HttpListenerContext context, string text, string contentType)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = data.Length;
            context.Response.OutputStream.Write(data, 0, data.Length);
            context.Response.OutputStream.Close();
        }

        private static string StateJson(ScannerState state)
        {
            return "{\"connected\":" + (state.Connected ? "true" : "false") +
                ",\"status\":\"" + Json(state.Status) +
                "\",\"deviceName\":\"" + Json(state.DeviceName) +
                "\",\"deviceDetails\":\"" + Json(state.DeviceDetails) +
                "\",\"serialNumber\":\"" + Json(state.SerialNumber) +
                "\",\"connectionType\":\"" + Json(state.ConnectionType) + "\"}";
        }

        private static string ResultJson(CommandResult result)
        {
            return "{\"success\":" + (result.Success ? "true" : "false") +
                ",\"response\":\"" + Json(result.Response) +
                "\",\"state\":" + StateJson(result.State) + "}";
        }

        private static string Json(string value)
        {
            if (value == null)
                return "";
            var builder = new StringBuilder();
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 32)
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            builder.Append(c);
                        break;
                }
            }
            return builder.ToString();
        }

        private static string LoadHtml()
        {
            string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
            if (File.Exists(file))
                return File.ReadAllText(file, Encoding.UTF8);

            file = Path.Combine(Environment.CurrentDirectory, "index.html");
            if (File.Exists(file))
                return File.ReadAllText(file, Encoding.UTF8);

            return Html();
        }

        private static string Html()
        {
            return @"<!doctype html>
<html lang=""ja"">
<head>
  <meta charset=""utf-8"">
  <title>Honeywell バーコードリーダー デバイス情報</title>
  <style>
    body{font-family:'Yu Gothic UI','Meiryo',sans-serif;margin:0;background:#f4f6f8;color:#17202a}
    main{max-width:1180px;margin:0 auto;padding:34px 28px}
    h1{font-size:34px;margin:0 0 24px}
    .row{display:flex;gap:12px;align-items:center;margin:12px 0}
    label{font-weight:700;min-width:100px}
    input{font-size:17px;padding:8px 10px;border:1px solid #c9d1d9;border-radius:4px}
    input.command{flex:1}
    button{font-size:16px;padding:9px 24px;border:1px solid #b8c2cc;border-radius:6px;background:white;cursor:pointer}
    button.primary{background:#1976d2;color:white;border-color:#1976d2}
    button:disabled{opacity:.55;cursor:default}
    .status{font-weight:700;margin-top:20px}
    .ok{color:#159447}.ng{color:#c62828}.wait{color:#d68100}
    .details{white-space:pre-wrap;color:#65717d;line-height:1.45;margin:8px 0 18px}
    .serial-label{font-weight:700;margin-top:12px}
    .serial{font-size:52px;font-weight:800;color:#144a82;line-height:1.15;margin:6px 0 22px}
    .response-head{display:flex;align-items:center;gap:12px;margin-top:22px}
    .response-head h2{font-size:22px;margin:0;flex:1}
    textarea{width:100%;height:330px;box-sizing:border-box;font-family:Consolas,monospace;font-size:17px;padding:12px;border:1px solid #c9d1d9;border-radius:4px;background:white;white-space:pre;overflow:auto}
    .note{color:#65717d;margin-top:8px}
  </style>
</head>
<body>
<main>
  <h1>Honeywell バーコードリーダー デバイス情報</h1>
  <div class=""row"">
    <label>接続デバイス</label>
    <input id=""device"" style=""flex:1"" readonly>
    <button id=""detect"">再検出</button>
  </div>
  <div class=""row"">
    <input id=""command"" class=""command"" autocomplete=""off"">
    <button id=""send"">コマンド送信</button>
  </div>
  <div id=""status"" class=""status wait"">● 未接続</div>
  <div id=""details"" class=""details""></div>
  <div class=""serial-label"">シリアルNo</div>
  <div id=""serial"" class=""serial"">未接続</div>
  <button id=""settings"" class=""primary"">デバイス設定出力</button>
  <div class=""response-head"">
    <h2>応答結果</h2>
    <button id=""clear"">クリア</button>
    <button id=""copy"">コピー</button>
  </div>
  <textarea id=""response"" readonly></textarea>
  <div class=""note"">送信コマンド: TERMID?;PREBK2?;SUFBK2?;DFMBK3?;PLGFOE?;PLGDCE?;REVINF.</div>
</main>
<script>
const $=id=>document.getElementById(id);
function setBusy(b){$('detect').disabled=b;$('send').disabled=b;$('settings').disabled=b}
function applyState(s){
  $('device').value=s.deviceName||'';
  $('details').textContent=s.deviceDetails||'';
  $('serial').textContent=s.serialNumber||'未接続';
  $('status').textContent='● '+(s.status||'未接続');
  $('status').className='status '+(s.connected?'ok':((s.status||'').includes('待機')?'wait':'ng'));
  $('send').disabled=!s.connected;
  $('settings').disabled=!s.connected;
}
async function api(path, body){
  setBusy(true);
  try{
    const r=await fetch(path,{method:path==='/api/state'?'GET':'POST',body});
    return await r.json();
  }finally{setBusy(false)}
}
async function detect(){const s=await api('/api/detect');applyState(s)}
async function settings(){const r=await api('/api/settings');$('response').value=r.response||'';applyState(r.state)}
async function send(){
  const c=$('command').value.trim();
  if(!c){alert('送信するコマンドを入力してください。');return}
  const r=await api('/api/send','command='+encodeURIComponent(c));
  $('response').value=r.response||'';
  applyState(r.state);
}
$('detect').onclick=detect;
$('settings').onclick=settings;
$('send').onclick=send;
$('clear').onclick=()=>$('response').value='';
$('copy').onclick=()=>navigator.clipboard.writeText($('response').value||'');
$('command').addEventListener('keydown',e=>{if(e.key==='Enter')send()});
fetch('/api/state').then(r=>r.json()).then(applyState).then(detect);
</script>
</body>
</html>";
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _scanner.Dispose();
        }
    }
}
