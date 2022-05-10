using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ZXing;

namespace WebDeck
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            const int SERVER_PORT = 25525;

            //サーバー立ち上げ
            WebServer webServer = new WebServer()
            {
                Port = SERVER_PORT
            };
            webServer.Start();

            //QR作る準備
            var writer = new ZXing.ImageSharp.BarcodeWriter<Rgba32>
            {
                Format = BarcodeFormat.QR_CODE,
                Options = new ZXing.Common.EncodingOptions
                {
                    Height = 128,
                    Width = 128,
                    Margin = 0
                }
            };

            //LocalIp取得
            var localIp = "";
            IPHostEntry entry = Dns.GetHostEntry(Dns.GetHostName());

            foreach (IPAddress ip in entry.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    localIp = ip.ToString();
                    break;
                }
            }

            //QRを作成（rootURL）
            var image = writer.Write("http://" + localIp + ":" + SERVER_PORT + "/");
            //保存
            image.SaveAsBmp(@".\WebDeckAccessQR.bmp");
            
            //WPFに表示
            QrCode.Source = new BitmapImage(new Uri(Path.GetFullPath(@".\WebDeckAccessQR.bmp"), UriKind.Absolute));

        }

        private void SmallBtn_Click(object sender, RoutedEventArgs e)
        {
            //全部隠す！
            ShowInTaskbar = false;
            Hide();
        }
    }

    public class WebServer
    {

        private int _port = 25525;
        public int Port
        {
            get { return _port; }
            set
            {
                if (value >= 1 && value <= 65535)
                {
                    _port = value;
                }
                else
                {
                    throw (new Exception("port out of range. allow 1~65535"));
                }
            }
        }

        private int _readTimeoutMs = 10000;
        public int ReadTimeoutMs { get { return _readTimeoutMs; } set { _readTimeoutMs = value; } }

        private int _writeTimeoutMs = 10000;
        public int WriteTimeoutMs { get { return _writeTimeoutMs; } set { _writeTimeoutMs = value; } }

        private IPAddress _acceptIp = IPAddress.Any;
        private Task? _server;
        private bool _serverStop = false;

        public void Start()
        {
            //サーバーを1台のみ起動
            _serverStop = false;
            if (_server == null)
            {
                Console.WriteLine("ServerLaunch");
                _server = Task.Run(Server);
            }
        }

        //abolished 期間足りない
        public void Stop()
        {
            _serverStop = true;
        }

        private void Server()
        {

            TcpListener listener = new TcpListener(_acceptIp, Port);
            Console.WriteLine("ServerListen:Port" + Port);
            listener.Start();

            //MainLoop
            while (!_serverStop)
            {
                Console.WriteLine("WaiteClient...");
                using (TcpClient client = listener.AcceptTcpClient())
                {
                    Console.WriteLine("Connected!");
                    Console.WriteLine("ClientConnected:" + client.Client.RemoteEndPoint);

                    using (NetworkStream networkStream = client.GetStream())
                    {
                        Console.WriteLine("Set ReadTimeout:" + ReadTimeoutMs + " WriteTimeout:" + WriteTimeoutMs);
                        networkStream.ReadTimeout = ReadTimeoutMs;
                        networkStream.WriteTimeout = WriteTimeoutMs;

                        //ストリーム用バッファ・Http用Stringビルダ　確保
                        byte[] requestReadBuffer = new byte[1024];
                        StringBuilder requestMsg = new StringBuilder();
                        int numberOfBytesRead = 0;

                        //Stream読み込みLoop
                        do
                        {
                            try
                            {
                                //バッファに読み込み
                                numberOfBytesRead = networkStream.Read(requestReadBuffer, 0, requestReadBuffer.Length);

                                //何も読まなければ　接続終了の合図
                                if (numberOfBytesRead <= 0)
                                {
                                    Console.WriteLine("ConectedClose");
                                    break;
                                }
                            }
                            catch (Exception e)
                            {
                                //強制切断時
                                Console.WriteLine("ForceDisconected");
                                break;
                            }

                            requestMsg.AppendFormat("{0}", Encoding.UTF8.GetString(requestReadBuffer, 0, numberOfBytesRead));
                        }
                        while (networkStream.DataAvailable);

                        //受信メッセージは長いので改行して表記
                        Console.WriteLine("RequestMessage↓");
                        Console.WriteLine(requestMsg.ToString());

                        //Request＋headerを分解、Request部分をLog　
                        string[] requestMsgLine = requestMsg.ToString().Split("\r\n");
                        Console.WriteLine("HttpRequest:" + requestMsgLine[0]);

                        //Request1行目、つまりGet,Postとかを分解、メソッドをLog
                        string[] httpRequest = requestMsgLine[0].Split(" ");
                        Console.WriteLine("HttpMethod:" + httpRequest[0]);

                        //responseを準備
                        string httpResponse = "";
                        //response作るやつ
                        HttpRequestProcess httpRequestProcess = HttpRequestProcess.GetInstance();
                        switch (httpRequest[0])
                        {
                            //Getメソッドならば
                            case "GET":
                                httpResponse = httpRequestProcess.GETRequest(requestMsgLine);
                                break;
                        }

                        Span<byte> responseBuffer = Encoding.UTF8.GetBytes(httpResponse);
                        networkStream.Write(responseBuffer);
                    }
                }

                Console.WriteLine("ClientDisconnected");

            }

            listener.Stop();
        }

        class HttpRequestProcess
        {

            const string SERVER_NAME = "WebDeckServer";

            const string HTTP_OK = "HTTP/1.0 200 OK";

            const string TEXT_CONTENT = "text/html";

            private static string _wwwDirectory = @".\www\";
            public static string WWWDirectory { get { return _wwwDirectory; } set { _wwwDirectory = value; } }

            private static string _indexFile = @"index.html";
            public static string IndexFile { get { return _indexFile; } set { _indexFile = value; } }

            private static string _notFoundFile = @"index.html";
            public static string NotFoundFile { get { return _notFoundFile; } set { _notFoundFile = value; } }


            //コマンド名、プロセスパス
            Dictionary<string, string> cmdDic = new Dictionary<string, string>();

            //Jsonでクライアントに送る用クラス（コマンドセット(1コマンドセット分））
            public class WebInfo
            {
                public string Title { get; set; }
                public string Cmd { get; set; }
                public string Icon { get; set; }
            }

            //ファイル読み込みとかの都合でコンストラクタは使いたいが、何度もファイルAccessなど気持ち悪いので（並列化したら衝突するし）将来に向けてシングルトン化
            //staticにすると誰か初期化を忘れるだろう
            static HttpRequestProcess? _instance = null;
            public static HttpRequestProcess GetInstance()
            {
                if(_instance == null)
                {
                    _instance = new HttpRequestProcess();
                }

                return _instance;
            }

            //シングルトン
            private HttpRequestProcess()
            {
                //シリアライズ用にリストを用意　Json:[{コマンドセット}...]
                List<WebInfo> webInfos = new List<WebInfo>();

                //Deckファイル読み込み(1行ずつ)
                foreach (string line in File.ReadLines(@".\deck.csv"))
                {
                    //Title[0],CMD-XXX[1],IconPath[2],ProcessPath[3]
                    var cmd = line.Split(",");

                    //IconPath Open
                    using (FileStream fileStream = new FileStream(cmd[2], FileMode.Open, FileAccess.Read))
                    {
                        byte[] imgRaw = new byte[fileStream.Length];
                        fileStream.Read(imgRaw, 0, imgRaw.Length);

                        //BytePngをBase64にして設定、コマンドセット作成
                        webInfos.Add(new WebInfo
                        {
                            Title = cmd[0],
                            Cmd = cmd[1],
                            Icon = Convert.ToBase64String(imgRaw)
                        });

                        //cmd実行用にコマンド名、プロセスパスを追加
                        cmdDic.Add(cmd[1], cmd[3]);
                        
                    }
                }

                //送信用Jsonを作成
                using (StreamWriter writer = new StreamWriter(@"./deck.json", false))
                {
                    writer.Write(JsonSerializer.Serialize(webInfos));
                }
                
            }

            public string GETRequest(string[] requestMsgLines)
            {
                //Requestからパラメータとか取得　あと　安全化
                GET get = new GET(requestMsgLines[0]);

                Console.WriteLine("GETRequestPath:" + get.RequestPath);
                Console.WriteLine("GETPhysicalPath:" + get.PhysicalPath);
                Console.WriteLine("GETRequestParameter:" + get.Param);

                //Requestされたファイルを読み込み
                string body;
                body = File.ReadAllText(get.PhysicalPath);

                //WebDeck特別処理部分
                if (get.Param == "REQTOP")
                    body = GetDeckDataJson();
                else if (get.Param != "")
                    body = RunCmd(get.Param);

                //下記定型文
                string httpResponse = "";
                httpResponse += HTTP_OK + "\r\n";
                httpResponse += SERVER_NAME + "\r\n";
                httpResponse += "Content-Length: " + body.Length + "\r\n";
                httpResponse += "Content-Type: " + TEXT_CONTENT + "\r\n";                           
                httpResponse += "Access-Control-Allow-Origin: *" + "\r\n";

                httpResponse += "\r\n";

                httpResponse += body + "\r\n";

                return httpResponse;
            }

            //送信用Json読み込み
            private string GetDeckDataJson()
            {
                return File.ReadAllText(@".\deck.json");
            }

            //Process起動
            private string RunCmd(string cmdName)
            {
                Process.Start(cmdDic[cmdName]);

                return "OK";
            }

            private class GET
            {
                //クライアントから要求されたパス　/../../info.log
                public string RequestPath { get; set; }

                //安全な物理パス　/../../info.log->/index.html
                public string PhysicalPath { get; set; }

                //パラメータ ?以降
                public string Param { get; set; }

                public GET(string param)
                {
                    //GET /index.html?CMD-A HTTP/1.1
                    //url+paramを取得
                    string url = param.Split(" ")[1];

                    //url[0]param[1]に分解
                    string[] urlBlock = url.Split("?");

                    //paramがない可能性あるので三項
                    Param = urlBlock.Length > 1 ? urlBlock[1] : "";

                    //プロパティ初期化
                    RequestPath = urlBlock[0];
                    PhysicalPath = Request2SafePath(urlBlock[0]);
                }

                //安全なパスを取得
                string Request2SafePath(string reqPath)
                {

                    //絶対パスに変換
                    // req"/../../info.log"->"C:\WWWDir\./../../info.log"->"C:\WWWDir\.\..\..\info.log"->"C:\info.log"
                    string reqPhysicalPath = Path.GetFullPath(WWWDirectory + @"." + reqPath.Replace(@"/", @"\"));

                    //最後が\で終わっていればindex.htmlを追加
                    if (reqPhysicalPath.ToCharArray()[reqPhysicalPath.Length - 1] == '\\')
                    {
                        reqPhysicalPath += IndexFile;
                    }

                    //安全な絶対パス　C:\WWWDir\
                    string safePhysicalPath = Path.GetFullPath(WWWDirectory);

                    try
                    {
                        //安全な絶対パス==reqPhysicalPathの先頭（安全絶対パス文字分）
                        if (safePhysicalPath == reqPhysicalPath.Substring(0, safePhysicalPath.Length))
                        {
                            //C:\WWWDir\下層が保証

                            //ファイルが存在するか
                            if (File.Exists(reqPhysicalPath))
                            {
                                safePhysicalPath = reqPhysicalPath;
                            }
                            else
                            {
                                //404NotFound
                                safePhysicalPath = Path.GetFullPath(WWWDirectory + @".\" + NotFoundFile);
                            }
                        }
                        else
                        {
                            //unsafe
                            //トップページに飛ばす
                            safePhysicalPath += IndexFile;
                        }
                    }
                    catch
                    {
                        //unsafe　安全パス分の文字数すらない
                        //トップページに飛ばす
                        safePhysicalPath +=　IndexFile;
                    }

                    return safePhysicalPath;
                }
            }
        }
    }
}
