using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class UDPReceiver : MonoBehaviour
{
    UdpClient udpClient;

    [Header("長方形（直線）移動の調整")]
    public float scaleX = 1.0f;  // 左右の移動量（倍率）
    public float scaleY = 1.0f;  // 上下の移動量（倍率）
    public float scaleZ = 0.01f;  // 奥行きの移動量（倍率。逆動する場合はマイナスにする）

    [Header("基本位置のオフセット")]
    // カメラの基準となる初期位置（画面の中心からの距離など）
    public Vector3 baseOffset = new Vector3(0f, 0f, 0f); 

    [Header("扇の中心（モニターの中心）")]
    public Transform lookTarget;

    [Header("スムージング設定")]
    [Range(1f, 50f)]
    public float lerpSpeed = 15f; 

    private Vector3 targetCamPos;

    void Start()
    {
        Application.targetFrameRate = 60;
        udpClient = new UdpClient(5005);
        targetCamPos = transform.position;
    }

    void Update()
    {
        if (udpClient != null && udpClient.Available > 0)
        {
            try
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = udpClient.Receive(ref ep);
                string text = Encoding.UTF8.GetString(data);
                string[] parts = text.Split(',');

                if (parts.Length >= 3 && lookTarget != null)
                {
                    float rawX = float.Parse(parts[0]);
                    float rawY = float.Parse(parts[1]);
                    float rawZ = float.Parse(parts[2]);

                    float moveX = rawX * scaleX;
                    float moveY = rawY * scaleY;
                    float moveZ = rawZ * scaleZ; 

                    Vector3 center = lookTarget.position;
                    
                    targetCamPos = new Vector3(
                        center.x + moveX + baseOffset.x,
                        center.y + moveY + baseOffset.y,
                        center.z + moveZ + baseOffset.z
                    );
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("UDP Receive Error: " + e.Message);
            }
        }

        // 毎フレーム滑らかに移動し、ターゲットを向き続ける
        if (targetCamPos != Vector3.zero)
        {
            transform.position = Vector3.Lerp(transform.position, targetCamPos, Time.deltaTime * lerpSpeed);
            
            // ★ここを追加：常にターゲットを向く
            transform.LookAt(lookTarget.position);
        }
    }

    void OnApplicationQuit()
    {
        if (udpClient != null) udpClient.Close();
    }
}