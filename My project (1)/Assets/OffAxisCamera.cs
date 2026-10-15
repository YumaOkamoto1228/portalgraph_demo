using UnityEngine;

// 現実のモニターを「窓」として扱い、目の位置から見た窓の向こう側を描画する（Off-Axis Projection）
// ・カメラの位置 = 現実の目の位置を仮想空間に写したもの
// ・カメラの向き = 常にスクリーンに垂直（LookAt はしない）
// ・投影行列   = 目からスクリーンの四隅を通る歪んだ視錐台
[RequireComponent(typeof(Camera))]
public class OffAxisCamera : MonoBehaviour
{
    public UDPReceiver tracker;

    [Header("仮想スクリーン（＝現実のモニター面）")]
    [Tooltip("モニターの中心に置く空オブジェクト。青矢印(Z+)をシーンの奥へ向ける。未設定ならカメラの初期位置・向きを使う")]
    public Transform screen;
    [Tooltip("モニターの表示部分の実寸・横（メートル）。27インチ16:9なら約0.597")]
    public float screenWidth = 0.597f;
    [Tooltip("モニターの表示部分の実寸・縦（メートル）。27インチ16:9なら約0.336")]
    public float screenHeight = 0.336f;
    [Tooltip("現実1メートルを Unity で何単位とするか。大きくするとシーンがミニチュア（フィギュア）のように見える")]
    public float worldScale = 4f;

    [Header("Webカメラの取り付け位置")]
    [Tooltip("モニター中心から見たレンズの位置（メートル）。x:右+ y:上+ z:手前+。上辺中央なら y = 画面の縦/2 + 枠の太さ")]
    public Vector3 webcamOffset = new Vector3(0f, 0.18f, 0f);
    [Tooltip("Webカメラが下向きに傾いている角度（度）")]
    public float webcamPitch = 0f;

    public enum DepthMode { Window, Dolly }

    [Header("前後の動きの扱い")]
    [Tooltip("Window: 現実の窓と同じ（近づくと画角が広がり、キャラは小さく見える）\nDolly: 画角は固定のまま、カメラ自体が前後に動く（近づくとキャラが大きく見える）")]
    public DepthMode depthMode = DepthMode.Dolly;
    [Tooltip("普段座っている距離（メートル）。この距離で見え方が基準になる")]
    public float depthGainCenter = 0.6f;
    [Tooltip("Window用: 前後(z)の動きの倍率。1=現実どおり")]
    public float depthGain = 1f;
    [Tooltip("Dolly用: 現実で1m前後に動いたとき、カメラを何m動かすか（worldScale倍される）")]
    public float dollyGain = 3f;

    [Header("描画")]
    [Tooltip("ニアクリップ（Unity単位）。小さすぎると奥行きの精度が落ち、重なった面がちらつく（Zファイティング）")]
    public float nearClip = 0.1f;

    Camera cam;

    void Start()
    {
        cam = GetComponent<Camera>();
        if (tracker == null) tracker = GetComponent<UDPReceiver>();

        if (screen == null)
        {
            // 未設定なら、カメラの初期位置・向きをそのままスクリーンにする
            screen = new GameObject("VirtualScreen (auto)").transform;
            screen.SetPositionAndRotation(transform.position, transform.rotation);
        }
    }

    // 単位の入力ミス（cmで入れてしまう等）に気付けるようにする
    void OnValidate()
    {
        if (screenWidth > 3f || screenHeight > 3f || webcamOffset.magnitude > 1f)
            Debug.LogWarning("OffAxisCamera: Screen Width/Height と Webcam Offset はメートル単位です（例: 18cm → 0.18）");
    }

    // 目の位置（スクリーン基準・メートル）。スクリーンの手前（見ている人の側）が -Z
    // dolly: Dollyモードでカメラをスクリーンの奥方向へ動かす量（メートル、近づくと+）
    Vector3 EyeOnScreen(out float dolly)
    {
        Vector3 head = tracker != null ? tracker.Head : new Vector3(0f, 0f, 0.6f);
        dolly = 0f;
        if (depthMode == DepthMode.Dolly)
        {
            // 画角を決める距離は固定し、前後のずれはカメラの移動に回す
            dolly = (depthGainCenter - head.z) * dollyGain;
            head.z = depthGainCenter;
        }
        else
        {
            head.z = depthGainCenter + (head.z - depthGainCenter) * depthGain;
        }

        // Webカメラの傾きを補正（カメラ座標: z は手前方向が+）
        float p = webcamPitch * Mathf.Deg2Rad;
        Vector3 h = new Vector3(
            head.x,
            head.y * Mathf.Cos(p) - head.z * Mathf.Sin(p),
            head.y * Mathf.Sin(p) + head.z * Mathf.Cos(p));

        h += webcamOffset;
        return new Vector3(h.x, h.y, -h.z);
    }

    void LateUpdate()
    {
#if ENABLE_INPUT_SYSTEM
        // ビルドしたアプリを Esc で終了できるようにする
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) Application.Quit();
#endif

        if (screen == null) return;

        Vector3 eye = EyeOnScreen(out float dolly) * worldScale;
        dolly *= worldScale;
        float halfW = screenWidth * worldScale * 0.5f;
        float halfH = screenHeight * worldScale * 0.5f;

        // 目からスクリーン面までの距離（画面の裏に回り込んだら破綻するので最低値を設ける）
        float d = Mathf.Max(-eye.z, 0.05f * worldScale);
        eye.z = -d;

        // カメラはスクリーンと同じ向きで、目の位置に置く
        // （Dollyでは窓ごと前後に動かすので、投影行列は eye のままでよい）
        Vector3 local = eye + Vector3.forward * dolly;
        transform.SetPositionAndRotation(screen.position + screen.rotation * local, screen.rotation);

        float n = Mathf.Min(nearClip, d * 0.9f);
        float f = cam.farClipPlane;
        cam.nearClipPlane = n;

        // スクリーンの四隅を near 面に投影した範囲
        float l = (-halfW - eye.x) * n / d;
        float r = ( halfW - eye.x) * n / d;
        float b = (-halfH - eye.y) * n / d;
        float t = ( halfH - eye.y) * n / d;

        cam.projectionMatrix = Matrix4x4.Frustum(l, r, b, t, n, f);
    }

    // シーンビューにスクリーン枠と目の位置を表示（配置調整用）
    void OnDrawGizmos()
    {
        Transform s = screen != null ? screen : transform;
        if (screen == null && Application.isPlaying) return;

        float hw = screenWidth * worldScale * 0.5f;
        float hh = screenHeight * worldScale * 0.5f;
        Vector3 c0 = s.position + s.rotation * new Vector3(-hw, -hh, 0f);
        Vector3 c1 = s.position + s.rotation * new Vector3( hw, -hh, 0f);
        Vector3 c2 = s.position + s.rotation * new Vector3( hw,  hh, 0f);
        Vector3 c3 = s.position + s.rotation * new Vector3(-hw,  hh, 0f);

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(c0, c1);
        Gizmos.DrawLine(c1, c2);
        Gizmos.DrawLine(c2, c3);
        Gizmos.DrawLine(c3, c0);

        if (!Application.isPlaying) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawSphere(transform.position, 0.03f * worldScale);
        Gizmos.DrawLine(transform.position, c0);
        Gizmos.DrawLine(transform.position, c1);
        Gizmos.DrawLine(transform.position, c2);
        Gizmos.DrawLine(transform.position, c3);
    }
}
