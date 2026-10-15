using UnityEngine;

// 背景の箱の入口を、仮想スクリーン（＝現実のモニターの枠）にぴったり合わせて配置する
// 壁の内側の面がスクリーンの四辺と一致し、壁はスクリーンより奥にだけ伸びる。
// こうするとモニター枠と壁の交差点がスクリーン面上に乗り、顔を動かしても動かなくなる。
// ※ エディタ上（再生していないとき）に、Main Camera の位置をスクリーンとして配置する
[ExecuteAlways]
public class ScreenBox : MonoBehaviour
{
    public OffAxisCamera offAxis;

    [Header("壁（Cubeを割り当てる。未設定の面は作らない）")]
    public Transform floor;
    public Transform ceiling;
    public Transform leftWall;
    public Transform rightWall;
    public Transform backWall;

    [Header("箱の形")]
    [Tooltip("スクリーンから奥の壁までの距離（Unity単位）")]
    public float depth = 3f;
    [Tooltip("壁の厚み（Unity単位）")]
    public float thickness = 0.05f;

    [Header("グリッド")]
    [Tooltip("マス目の線がスクリーンの角から始まるように WorldGrid の基準位置を合わせる")]
    public bool alignGridToScreen = true;

    static readonly int GridOffsetId = Shader.PropertyToID("_GridOffset");

    // グリッドの基準位置（MaterialPropertyBlock）はシーンに保存されないので、再生開始時にも設定する
    // （Start はカメラが動き出す LateUpdate より前に呼ばれる）
    void Start()
    {
        if (offAxis != null) Layout();
    }

    void Update()
    {
        // 再生中はカメラが動くので配置し直さない
        if (Application.isPlaying || offAxis == null) return;
        Layout();
    }

    void Layout()
    {
        // OffAxisCamera と同じく、Screen 未設定ならカメラの位置・向きをスクリーンとする
        Transform s = offAxis.screen != null ? offAxis.screen : offAxis.transform;
        float hw = offAxis.screenWidth * offAxis.worldScale * 0.5f;
        float hh = offAxis.screenHeight * offAxis.worldScale * 0.5f;
        float t = thickness;
        float d = depth;

        // スクリーン基準の座標（x:右 y:上 z:奥）で、内側の面が四辺に一致するよう置く
        Place(s, floor,     new Vector3(0f, -hh - t / 2, d / 2),          new Vector3(2 * hw + 2 * t, t, d));
        Place(s, ceiling,   new Vector3(0f,  hh + t / 2, d / 2),          new Vector3(2 * hw + 2 * t, t, d));
        Place(s, leftWall,  new Vector3(-hw - t / 2, 0f, d / 2),          new Vector3(t, 2 * hh, d));
        Place(s, rightWall, new Vector3( hw + t / 2, 0f, d / 2),          new Vector3(t, 2 * hh, d));
        Place(s, backWall,  new Vector3(0f, 0f, d + t / 2),               new Vector3(2 * hw + 2 * t, 2 * hh + 2 * t, t));

        if (alignGridToScreen)
        {
            Vector3 corner = s.position + s.rotation * new Vector3(-hw, -hh, 0f);
            var block = new MaterialPropertyBlock();
            foreach (var w in new[] { floor, ceiling, leftWall, rightWall, backWall })
            {
                if (w == null) continue;
                var r = w.GetComponent<Renderer>();
                if (r == null) continue;
                r.GetPropertyBlock(block);
                block.SetVector(GridOffsetId, corner);
                r.SetPropertyBlock(block);
            }
        }
    }

    static void Place(Transform screen, Transform wall, Vector3 localPos, Vector3 size)
    {
        if (wall == null) return;
        // ※ 壁は Scale が (1,1,1) 以外の親の子にしないこと（大きさがずれる）
        wall.SetPositionAndRotation(screen.position + screen.rotation * localPos, screen.rotation);
        wall.localScale = size;
    }
}
