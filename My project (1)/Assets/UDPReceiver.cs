using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// tracker.py から「目の位置（Webカメラ基準・メートル）」を受信し、One Euro Filter で平滑化する
// 受信データ: "x,y,z"  x:見ている人の右が+ / y:上が+ / z:Webカメラからの距離
public class UDPReceiver : MonoBehaviour
{
    [Header("受信設定")]
    public int port = 5005;

    [Header("向きが逆に動くときだけチェック")]
    public bool invertX = false;
    public bool invertY = false;

    [Header("One Euro Filter（ブレと遅延のバランス）")]
    [Tooltip("小さいほど静止時のブレが減るが、ゆっくりした動きが遅れる (Hz)")]
    public float minCutoff = 1.0f;
    [Tooltip("奥行き(z)はノイズが大きいので左右上下より弱めにかける (Hz)")]
    public float minCutoffZ = 0.5f;
    [Tooltip("大きいほど速い動きへの追従が良くなる（遅延が減る）")]
    public float beta = 5.0f;
    public float dCutoff = 1.0f;

    [Header("顔が見つからないときの目の位置（メートル）")]
    public Vector3 defaultHead = new Vector3(0f, 0f, 0.6f);

    // フィルタ後の目の位置（Webカメラ基準・メートル）
    public Vector3 Head
    {
        get { lock (lockObj) return hasData ? filtered : defaultHead; }
    }

    UdpClient udpClient;
    Thread thread;
    volatile bool running;
    readonly object lockObj = new object();
    Vector3 filtered;
    bool hasData;

    void Start()
    {
        Application.targetFrameRate = 60;
        udpClient = new UdpClient(port);
        running = true;
        thread = new Thread(ReceiveLoop) { IsBackground = true };
        thread.Start();
    }

    // 受信は別スレッドで行い、届いた順にフィルタへ通す（データが溜まって遅れるのを防ぐ）
    void ReceiveLoop()
    {
        var filterXY = new OneEuroFilter();
        var filterZ = new OneEuroFilter();
        var clock = Stopwatch.StartNew();
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (running)
        {
            try
            {
                byte[] data = udpClient.Receive(ref ep);
                string[] parts = Encoding.UTF8.GetString(data).Split(',');
                if (parts.Length < 3) continue;

                var raw = new Vector3(
                    float.Parse(parts[0], CultureInfo.InvariantCulture),
                    float.Parse(parts[1], CultureInfo.InvariantCulture),
                    float.Parse(parts[2], CultureInfo.InvariantCulture));
                if (invertX) raw.x = -raw.x;
                if (invertY) raw.y = -raw.y;

                double t = clock.Elapsed.TotalSeconds;
                // zのノイズで左右上下の速度推定が乱れないよう、別々にフィルタする
                Vector3 xy = filterXY.Filter(new Vector3(raw.x, raw.y, 0f), t, minCutoff, beta, dCutoff);
                Vector3 z = filterZ.Filter(new Vector3(0f, 0f, raw.z), t, minCutoffZ, beta, dCutoff);

                lock (lockObj)
                {
                    filtered = new Vector3(xy.x, xy.y, z.z);
                    hasData = true;
                }
            }
            catch (SocketException) { /* 終了時の Close で発生する */ }
            catch (ObjectDisposedException) { }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("UDP Receive Error: " + e.Message);
            }
        }
    }

    void OnDestroy()
    {
        running = false;
        if (udpClient != null) udpClient.Close();
    }

    // 速く動いたときは追従を優先し、止まっているときはブレを強く抑えるフィルタ
    // https://gery.casiez.net/1euro/
    class OneEuroFilter
    {
        Vector3 prevX, prevDx;
        double prevT;
        bool initialized;

        public Vector3 Filter(Vector3 x, double t, float minCutoff, float beta, float dCutoff)
        {
            if (!initialized)
            {
                initialized = true;
                prevX = x;
                prevDx = Vector3.zero;
                prevT = t;
                return x;
            }

            float dt = Mathf.Max((float)(t - prevT), 1e-4f);
            prevT = t;

            Vector3 dx = (x - prevX) / dt;
            prevDx = Vector3.Lerp(prevDx, dx, Alpha(dCutoff, dt));

            float cutoff = minCutoff + beta * prevDx.magnitude;
            prevX = Vector3.Lerp(prevX, x, Alpha(cutoff, dt));
            return prevX;
        }

        static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }
}
