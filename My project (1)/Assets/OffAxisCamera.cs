using UnityEngine;

public class OffAxisCamera : MonoBehaviour
{
    public Camera cam;
    public Transform screenBottomLeft;
    public Transform screenBottomRight;
    public Transform screenTopLeft;

    void Start()
    {
        // アタッチされているカメラを自動取得
        if (cam == null) cam = GetComponent<Camera>();
    }

    void LateUpdate()
    {
        // 四隅のオブジェクトが未設定なら計算しない
        if (screenBottomLeft == null || screenBottomRight == null || screenTopLeft == null) return;

        // モニターの頂点座標を取得
        Vector3 pa = screenBottomLeft.position;
        Vector3 pb = screenBottomRight.position;
        Vector3 pc = screenTopLeft.position;
        
        // 自分の位置（UDPReceiverで動かされたカメラの位置）を視点とする
        Vector3 pe = transform.position;

        // モニターの向き（右、上、正面のベクトル）を計算
        Vector3 vr = (pb - pa).normalized;
        Vector3 vu = (pc - pa).normalized;
        Vector3 vn = Vector3.Cross(vr, vu).normalized;

        // 視点から各頂点へのベクトル
        Vector3 va = pa - pe;
        Vector3 vb = pb - pe;
        Vector3 vc = pc - pe;

        // 視点からモニター平面までの垂直距離
        float d = -Vector3.Dot(va, vn);
        
        // 距離が近すぎる、または画面の裏側にいる時は描画が破綻するので計算しない
        if (d <= 0.001f) return;

        float n = cam.nearClipPlane;
        float f = cam.farClipPlane;

        // 視錐台（カメラに映る範囲）の歪みを計算
        float l = Vector3.Dot(vr, va) * n / d;
        float r = Vector3.Dot(vr, vb) * n / d;
        float b = Vector3.Dot(vu, va) * n / d;
        float t = Vector3.Dot(vu, vc) * n / d;

        // 投影行列(Projection Matrix)の組み立て
        Matrix4x4 p = new Matrix4x4();
        p[0, 0] = 2.0f * n / (r - l);
        p[0, 2] = (r + l) / (r - l);
        p[1, 1] = 2.0f * n / (t - b);
        p[1, 2] = (t + b) / (t - b);
        p[2, 2] = -(f + n) / (f - n);
        p[2, 3] = -2.0f * f * n / (f - n);
        p[3, 2] = -1.0f;
        p[3, 3] = 0.0f;

        // カメラに歪みを適用
        cam.projectionMatrix = p;
        
        // カメラの向きを強制的にモニターの正面に向ける
        //cam.transform.rotation = Quaternion.LookRotation(-vn, vu);
    }
}