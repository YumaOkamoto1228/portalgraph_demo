using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// tracker.py から「目の位置（Webカメラ基準・メートル）」を受信し、One Euro Filter で平滑化する
// 受信は約30fpsだが、描画（60fps）のたびに速度から現在位置を予測するので、視点は毎フレーム動く
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

    [Header("予測（カメラの約30fpsの間を埋めて、視点を描画のたびに動かす）")]
    [Tooltip("オン: 最後に受信した位置と速度から「描画する時点の位置」を計算する（視点が最大60fpsで動く）\nオフ: 受信した位置をそのまま使う（視点は約30fpsでしか動かない）")]
    public bool prediction = true;
    [Tooltip("ソフトで測れない遅延（カメラ内部・モニターの表示）の分だけ、さらに先を読む時間（秒）。\n0 = 測れる分（撮影からの経過時間）だけ。大きくすると遅れは減るが、急に止まったときに行き過ぎる。目安 0〜0.05")]
    public float predictionLead = 0f;
    [Tooltip("先読みする時間の上限（秒）。顔を見失ったときに視点が動き続けないようにする")]
    public float maxPrediction = 0.1f;
    [Tooltip("速度の推定をなめらかにするカットオフ (Hz)。小さいほど静止時に安定するが、動き出しと止まるときの反応が遅れる")]
    public float velocityCutoff = 3.0f;

    [Header("顔が見つからないときの目の位置（メートル）")]
    public Vector3 defaultHead = new Vector3(0f, 0f, 0.6f);

    // 描画に使う目の位置（Webカメラ基準・メートル）。予測がオンなら「今この瞬間」の位置
    // 計算は「位置 + 速度 × 経過時間」だけなので、毎フレーム呼んでも負荷はない
    public Vector3 Head
    {
        get
        {
            Vector3 p, v;
            double t;
            lock (lockObj)
            {
                if (!hasData) return defaultHead;
                p = filtered;
                v = velocity;
                t = sampleTime;
            }
            return prediction ? p + v * Horizon(t) : p;
        }
    }

    // 最後に受信した位置（フィルタ後・予測なし）
    public Vector3 FilteredHead
    {
        get { lock (lockObj) return hasData ? filtered : defaultHead; }
    }

    // いま何秒先を読んでいるか（予測がオフ、または未受信なら 0）
    public float PredictionHorizon
    {
        get
        {
            double t;
            lock (lockObj)
            {
                if (!hasData) return 0f;
                t = sampleTime;
            }
            return prediction ? Horizon(t) : 0f;
        }
    }

    // 撮影時刻から描画時刻までの経過時間 + 追加の先読み。上限を設けて、見失ったときに動き続けないようにする
    float Horizon(double sampleT)
    {
        float elapsed = (float)(Clock.Elapsed.TotalSeconds - sampleT);
        return Mathf.Clamp(elapsed + predictionLead, 0f, maxPrediction);
    }

    // ===== 計測（PerfOverlay で表示） =====
    // 受信時刻・描画時刻の比較に使う共通の時計
    public static readonly Stopwatch Clock = Stopwatch.StartNew();

    public struct PerfStats
    {
        public float packetsPerSec;   // 受信レート（≒トラッカーの送信fps）
        public float intervalAvgMs;   // 受信間隔の平均
        public float intervalMaxMs;   // 受信間隔の最大（引っかかりの検出）
        public float procAvgMs;       // Python側: フレーム取得→送信の平均
        public float procMaxMs;
        public int lost;              // 通し番号の欠け（パケットロス）の数
    }

    public PerfStats Perf { get { lock (lockObj) return perf; } }

    // 最後にパケットを受信した時刻（Clock基準の秒）。未受信なら負
    public double LastPacketTime { get { lock (lockObj) return lastArrival; } }

    UdpClient udpClient;
    Thread thread;
    volatile bool running;
    readonly object lockObj = new object();
    Vector3 filtered, velocity;   // フィルタ後の位置と、その速度（m/s）
    double sampleTime;            // その位置が撮影された時刻（Clock基準の秒）
    bool hasData;

    PerfStats perf;
    double lastArrival = -1, windowStart;
    int windowCount, windowLost, intervalCount, lastSeq = -1;
    float intervalSum, intervalMax, procSum, procMax;

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
        Vector3 prevPos = Vector3.zero, vel = Vector3.zero;
        double prevT = -1;
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

                double arrival = Clock.Elapsed.TotalSeconds;

                // 4番目: Python側の処理時間(ms)、5番目: 通し番号（古い tracker.py なら無い）
                float procMs = parts.Length >= 4 ? float.Parse(parts[3], CultureInfo.InvariantCulture) : 0f;
                int seq = parts.Length >= 5 ? int.Parse(parts[4], CultureInfo.InvariantCulture) : -1;
                RecordPacket(arrival, procMs, seq);

                // この位置が撮影された時刻（受信時刻から Python 側の処理時間を引く）
                // 処理時間のばらつきが時間間隔に乗らないので、フィルタと速度の推定が安定する
                double t = arrival - procMs * 0.001;

                // zのノイズで左右上下の速度推定が乱れないよう、別々にフィルタする
                Vector3 xy = filterXY.Filter(new Vector3(raw.x, raw.y, 0f), t, minCutoff, beta, dCutoff);
                Vector3 z = filterZ.Filter(new Vector3(0f, 0f, raw.z), t, minCutoffZ, beta, dCutoff);

                var pos = new Vector3(xy.x, xy.y, z.z);

                // 予測に使う速度: フィルタ後の位置の変化量を、軽くなめらかにしたもの
                if (prevT >= 0)
                {
                    float dt = Mathf.Max((float)(t - prevT), 1e-4f);
                    // しばらく途切れた後（顔を見失っていた）は、古い速度を引きずらない
                    vel = dt > 0.25f
                        ? Vector3.zero
                        : Vector3.Lerp(vel, (pos - prevPos) / dt, OneEuroFilter.Alpha(velocityCutoff, dt));
                }
                prevPos = pos;
                prevT = t;

                lock (lockObj)
                {
                    filtered = pos;
                    velocity = vel;
                    sampleTime = t;
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

    // 受信間隔・処理時間・パケットロスを1秒ごとに集計する（受信スレッドから呼ぶ）
    void RecordPacket(double now, float procMs, int seq)
    {
        lock (lockObj)
        {
            if (lastArrival >= 0)
            {
                float interval = (float)(now - lastArrival) * 1000f;
                intervalSum += interval;
                intervalMax = Mathf.Max(intervalMax, interval);
                intervalCount++;
            }
            else
            {
                windowStart = now;
            }
            lastArrival = now;

            procSum += procMs;
            procMax = Mathf.Max(procMax, procMs);
            windowCount++;

            // 番号が飛んだ分をロスとして数える（tracker.py を再起動して番号が戻った場合は数えない）
            if (seq >= 0 && lastSeq >= 0 && seq > lastSeq + 1) windowLost += seq - lastSeq - 1;
            if (seq >= 0) lastSeq = seq;

            double elapsed = now - windowStart;
            if (elapsed >= 1.0)
            {
                perf = new PerfStats
                {
                    packetsPerSec = (float)(windowCount / elapsed),
                    intervalAvgMs = intervalCount > 0 ? intervalSum / intervalCount : 0f,
                    intervalMaxMs = intervalMax,
                    procAvgMs = procSum / windowCount,
                    procMaxMs = procMax,
                    lost = windowLost,
                };
                windowStart = now;
                windowCount = windowLost = intervalCount = 0;
                intervalSum = intervalMax = procSum = procMax = 0f;
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

        public static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }
}
