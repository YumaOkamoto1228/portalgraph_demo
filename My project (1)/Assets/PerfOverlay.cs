using UnityEngine;

// fps とトラッキング遅延の内訳を画面左上に表示する（F1で表示切り替え）
// UDPReceiver があるシーンでは、再生時に自動で追加される
//
// 遅延の内訳（ソフトで測れる範囲）
//   Python proc : フレーム取得 → UDP送信（tracker.py が測って送ってくる値）
//   Data age    : パケット受信 → そのデータを描画に使うまで（受信と描画のタイミング差）
//   Prediction  : 予測で先読みしている時間（撮影からの経過時間 + predictionLead）。この分の遅延は打ち消される
//   Frame       : 描画1フレーム分（GPUで描画して画面に出るまで、少なくとも1フレームかかる）
// ※ カメラ内部（露光〜PCへの転送）とモニターの表示遅延は含まない。
//   体感の合計遅延は、スマホのスロー撮影で頭の動きと画面の変化を撮って測る
public class PerfOverlay : MonoBehaviour
{
    public bool visible = true;

    UDPReceiver receiver;

    // Unity側の集計（1秒ごと）
    float windowStart, frameTimeMax, ageSum, ageMax;
    int frames, ageCount;
    float fps, frameAvgMs, frameMaxMs, ageAvgMs, ageMaxMs, horizonSum, horizonAvgMs;

    GUIStyle style;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoCreate()
    {
        if (FindAnyObjectByType<PerfOverlay>() != null) return;
        if (FindAnyObjectByType<UDPReceiver>() == null) return;
        new GameObject("PerfOverlay (auto)").AddComponent<PerfOverlay>();
    }

    void Start()
    {
        receiver = FindAnyObjectByType<UDPReceiver>();
        windowStart = Time.unscaledTime;
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null && keyboard.f1Key.wasPressedThisFrame) visible = !visible;
#endif
    }

    // OffAxisCamera と同じく LateUpdate で、描画に使う時点のデータの古さを測る
    void LateUpdate()
    {
        frames++;
        frameTimeMax = Mathf.Max(frameTimeMax, Time.unscaledDeltaTime);

        if (receiver != null && receiver.LastPacketTime >= 0)
        {
            float age = (float)(UDPReceiver.Clock.Elapsed.TotalSeconds - receiver.LastPacketTime) * 1000f;
            ageSum += age;
            ageMax = Mathf.Max(ageMax, age);
            ageCount++;
            horizonSum += receiver.PredictionHorizon * 1000f;
        }

        float elapsed = Time.unscaledTime - windowStart;
        if (elapsed >= 1f)
        {
            fps = frames / elapsed;
            frameAvgMs = elapsed * 1000f / frames;
            frameMaxMs = frameTimeMax * 1000f;
            ageAvgMs = ageCount > 0 ? ageSum / ageCount : 0f;
            ageMaxMs = ageMax;
            horizonAvgMs = ageCount > 0 ? horizonSum / ageCount : 0f;

            windowStart = Time.unscaledTime;
            frames = ageCount = 0;
            frameTimeMax = ageSum = ageMax = horizonSum = 0f;
        }
    }

    void OnGUI()
    {
        if (!visible) return;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label) { fontSize = 18 };
            style.normal.textColor = Color.white;
        }

        string text;
        if (receiver == null || receiver.LastPacketTime < 0)
        {
            text = $"Unity      {fps,5:F1} fps  (frame {frameAvgMs:F1} / max {frameMaxMs:F1} ms)\n" +
                   "Tracker    no data";
        }
        else
        {
            var p = receiver.Perf;
            double silence = UDPReceiver.Clock.Elapsed.TotalSeconds - receiver.LastPacketTime;
            float total = p.procAvgMs + ageAvgMs + frameAvgMs;
            // 目の位置（Webカメラ基準）。z をメジャーで測った距離と比べると、キャリブレーションのずれが分かる
            Vector3 eye = receiver.Head * 100f;
            text =
                $"Eye (cm)   x {eye.x,6:F1}  y {eye.y,6:F1}  z {eye.z,6:F1}  (from webcam)\n" +
                $"Unity      {fps,5:F1} fps  (frame {frameAvgMs:F1} / max {frameMaxMs:F1} ms)\n" +
                $"Tracker    {p.packetsPerSec,5:F1} pkt/s  (interval {p.intervalAvgMs:F1} / max {p.intervalMaxMs:F1} ms, lost {p.lost})\n" +
                (silence > 0.5 ? $"           no packet for {silence:F1} s (face lost?)\n" : "") +
                "--- latency (avg / max ms) ---\n" +
                $"Python proc {p.procAvgMs,6:F1} / {p.procMaxMs:F1}\n" +
                $"Data age    {ageAvgMs,6:F1} / {ageMaxMs:F1}\n" +
                $"Frame       {frameAvgMs,6:F1}\n" +
                $"Total (SW)  {total,6:F1}   + camera & display (not measured)\n" +
                // 予測で先読みしている時間。Python proc と Data age の分は、これで打ち消されている
                (receiver.prediction ? $"Prediction  {horizonAvgMs,6:F1}   ahead (lead {receiver.predictionLead * 1000f:F0})\n"
                                     : "Prediction     off\n") +
                "[F1] hide";
        }

        var size = style.CalcSize(new GUIContent(text));
        GUI.Box(new Rect(8, 8, size.x + 16, size.y + 12), GUIContent.none);
        GUI.Label(new Rect(16, 14, size.x, size.y), text, style);
    }
}
