using System;
using System.Collections.Generic;
using System.Drawing;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Linq;
using Newtonsoft.Json;
using System.IO;
using PdfiumViewer;
using System.Drawing.Imaging;
using System.Collections.Concurrent;
using System.Net;
using System.Web;
using LZ4;

class Program
{
    const int __DB = 15;
    const int _HTTP_PORT = 12311;
    const int __PORT_WRITE = 1000;
    const int __PORT_READ = 1001;
    const string __SUBCRIBE_IN = "__PDF_IN";
    const string __SUBCRIBE_OUT = "__PDF_OUT";
    static RedisBase m_subcriber;
    static bool __running = true;
    static ConcurrentDictionary<long, int[]> m_docPageSize = new ConcurrentDictionary<long, int[]>();

    #region [ + ]

    static byte[] _pageAsBitmapBytes(PdfDocument doc, int pageCurrent)
    {
        int w = (int)doc.PageSizes[pageCurrent].Width;
        int h = (int)doc.PageSizes[pageCurrent].Height;

        ////if (w >= h) w = this.Width;
        ////else w = 1200;
        //if (w < 1200) w = 1200;
        //h = (int)((w * doc.PageSizes[i].Height) / doc.PageSizes[i].Width);

        if (w > 1200)
        {
            w = 1200;
            h = (int)((w * doc.PageSizes[pageCurrent].Height) / doc.PageSizes[pageCurrent].Width);
        }

        using (var image = doc.RenderTransparentBG(pageCurrent, w, h, 100, 100))
        using (var ms = new MemoryStream())
        {
            image.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static void _updateDocInfo(string requestId, string file)
    {
        long docInfoId = 0, docId = 0;
        if (File.Exists(file))
        {
            var redis = new RedisBase(new RedisSetting(REDIS_TYPE.ONLY_WRITE, __PORT_WRITE));
            var cmd = COMMANDS.DOC_INFO.ToString();
            try
            {
                int pageTotal = 0;
                using (var doc = PdfDocument.Load(file))
                {
                    pageTotal = doc.PageCount;
                    long fileSize = new FileInfo(file).Length;
                    docInfoId = StaticDocument.BuildId(DOC_TYPE.INFO_OGRINAL, pageTotal, fileSize);
                    var docInfo = new oDocument()
                    {
                        id = docInfoId,
                        file_length = fileSize,
                        file_name_ascii = "",
                        file_name_ogrinal = Path.GetFileNameWithoutExtension(file),
                        file_page = pageTotal,
                        file_path = file,
                        file_type = DOC_TYPE.PDF_OGRINAL,
                        infos = doc.GetInformation().toDictionary(),
                        metadata = ""
                    };
                    var json = JsonConvert.SerializeObject(docInfo, Formatting.Indented);
                    var bsInfo = Encoding.UTF8.GetBytes(json);
                    var lz = LZ4.LZ4Codec.Wrap(bsInfo);
                    ////var decompressed = LZ4Codec.Unwrap(compressed);
                    redis.HSET("_DOC_INFO", docInfoId.ToString(), lz);
                }
                if (docInfoId > 0)
                    redis.ReplyRequest(requestId, cmd, 1, docInfoId, pageTotal, "", file);
            }
            catch (Exception exInfo)
            {
                string errInfo = cmd.ToString() + " -> " + file + Environment.NewLine + exInfo.Message + Environment.NewLine + exInfo.StackTrace;
                redis.HSET("_ERROR:PDF:" + cmd.ToString(), docInfoId.ToString(), errInfo);
            }
        }
    }

    static void _splitAllJpeg(string requestId, string file)
    {
    }

    static void _splitAllPng(string requestId, string file)
    {
        if (File.Exists(file))
        {
            var redis = new RedisBase(new RedisSetting(REDIS_TYPE.ONLY_WRITE, __PORT_WRITE));
            var cmd = COMMANDS.PDF_SPLIT_ALL_PNG.ToString();
            long docId = 0;
            try
            {
                using (var doc = PdfDocument.Load(file))
                {
                    int pageTotal = doc.PageCount;
                    docId = StaticDocument.BuildId(DOC_TYPE.PNG_OGRINAL, pageTotal, new FileInfo(file).Length);
                    var sizes = new Dictionary<string, string>();
                    for (int i = 0; i < pageTotal; i++)
                    {
                        byte[] buf = null;
                        string slen = "";
                        bool ok = false;
                        string err = "";
                        try
                        {
                            buf = _pageAsBitmapBytes(doc, i);
                            slen = buf.Length.ToString();
                            ok = redis.HSET(docId, i, buf);
                        }
                        catch (Exception ex)
                        {
                            err = ex.Message + Environment.NewLine + ex.StackTrace;
                        }
                        redis.ReplyRequest(requestId, cmd, ok ? 1 : 0, docId, i, "PROCESSING", file, err);
                        sizes.Add(string.Format("{0}:{1}", docId, i), slen);
                    }
                    redis.HMSET("_IMG_SIZE", sizes);
                    redis.ReplyRequest(requestId, cmd, 1, docId, pageTotal, "COMPLETE", file);
                }
            }
            catch (Exception exInfo)
            {
                string errInfo = cmd.ToString() + " -> " + file + Environment.NewLine + exInfo.Message + Environment.NewLine + exInfo.StackTrace;
                redis.HSET("_ERROR:PDF:" + cmd.ToString(), docId.ToString(), errInfo);
            }
        }
    }

    #endregion

    #region [ MMF - REDIS ]

    static void __getUrlWriteRedis(COMMANDS cmd, bool ok, string requestId, string url, string data)
    {
        var command = cmd.ToString();
        Uri uri = null;
        if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out uri))
        {
            string host = uri.Host.ToLower(), path = uri.PathAndQuery.Substring(1).ToLower();
            if (host.StartsWith("www.")) host = host.Substring(4);

            var buf = Encoding.UTF8.GetBytes(data);
            var lz = LZ4Codec.Wrap(buf);

            var redis = new RedisBase(new RedisSetting(REDIS_TYPE.ONLY_WRITE, __PORT_WRITE, __DB));
            var exist = redis.HEXISTS(host, path);
            if (!exist)
            {
                redis.HSET(host, path, lz);
                redis.ReplyRequest(requestId, command, 1, host, path);
            }
        }
    }

    #endregion

    static void __executeTaskHttp(Tuple<string, COMMANDS, string> data)
    {
        string requestId = data.Item1, input = data.Item3;
        COMMANDS cmd = data.Item2;
        switch (cmd)
        {
            case COMMANDS.DOC_INFO:
                _updateDocInfo(requestId, input);
                break;
            case COMMANDS.PDF_SPLIT_ALL_JPG:
                _splitAllJpeg(requestId, input);
                break;
            case COMMANDS.PDF_SPLIT_ALL_PNG:
                _splitAllPng(requestId, input);
                break;
        }
    }

    #region [ MAIN ]

    static void __executeTaskTcp(Tuple<string, byte[]> data)
    {
        if (data == null || data.Item2 == null || data.Item2.Length < 39) return;
        var buf = data.Item2;
        string requestId = Encoding.ASCII.GetString(buf, 0, 36);
        var cmd = (COMMANDS)((int)buf[36]);
        string text = Encoding.UTF8.GetString(buf, 37, buf.Length - 37);
        __executeTaskHttp(new Tuple<string, COMMANDS, string>(requestId, cmd, text));
    }

    static WebServer _http;
    static void __startApp()
    {
        string uri = string.Format("http://127.0.0.1:{0}/", _HTTP_PORT);
        _http = new WebServer(__executeTaskHttp);
        _http.Start(uri);
        m_subcriber = new RedisBase(new RedisSetting(REDIS_TYPE.ONLY_SUBCRIBE, __PORT_READ));
        m_subcriber.PSUBSCRIBE(__SUBCRIBE_IN);
        var bs = new List<byte>();
        while (__running)
        {
            if (!m_subcriber.m_stream.DataAvailable)
            {
                if (bs.Count > 0)
                {
                    var buf = m_subcriber.__getBodyPublish(bs.ToArray(), __SUBCRIBE_IN);
                    bs.Clear();
                    if (buf != null)
                        new Thread(new ParameterizedThreadStart((o) =>
                        __executeTaskTcp((Tuple<string, byte[]>)o))).Start(buf);
                }
                Thread.Sleep(100);
                continue;
            }
            byte b = (byte)m_subcriber.m_stream.ReadByte();
            bs.Add(b);
        }
    }

    static void __stopApp()
    {
        __running = false;
        _http.Stop();
    }

    // FOR SETTING OF WINDOWS SERVICE

    static Thread __threadWS = null;
    static void Main(string[] args)
    {
        if (Environment.UserInteractive)
        {
            Console.Title = string.Format("{0} - {1}", __SUBCRIBE_IN, _HTTP_PORT);
            StartOnConsoleApp(args);
            Console.WriteLine("Press any key to stop...");
            Console.ReadKey(true);
            Stop();
        }
        else using (var service = new MyService())
                ServiceBase.Run(service);
    }

    public static void StartOnConsoleApp(string[] args) => __startApp();
    public static void StartOnWindowService(string[] args)
    {
        __threadWS = new Thread(new ThreadStart(() => __startApp()));
        __threadWS.IsBackground = true;
        __threadWS.Start();
    }

    public static void Stop()
    {
        __stopApp();
        if (__threadWS != null) __threadWS.Abort();
    }
    static int __freeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    #endregion;
}

