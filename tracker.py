import cv2
import mediapipe as mp
import socket
import math

# パラメータ設定
W = 0.06      # 現実の両目の距離 (メートル)
f = 3000      # 事前に測定した焦点距離 (ピクセル) ※環境に合わせて後で調整
UDP_IP = "127.0.0.1"
UDP_PORT = 5005

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
mp_face_mesh = mp.solutions.face_mesh

# Anker PowerConf C200 (通常はインデックス0か1)
cap = cv2.VideoCapture(0)

cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1920)
cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 1080)

# 設定が反映されたか確認（カメラによっては最大値が制限されるため）
actual_w = cap.get(cv2.CAP_PROP_FRAME_WIDTH)
actual_h = cap.get(cv2.CAP_PROP_FRAME_HEIGHT)
print(f"Resolution: {actual_w}x{actual_h}")

with mp_face_mesh.FaceMesh(max_num_faces=1, refine_landmarks=True) as face_mesh:
    while cap.isOpened():
        success, image = cap.read()
        if not success:
            continue

        image_height, image_width, _ = image.shape
        results = face_mesh.process(cv2.cvtColor(image, cv2.COLOR_BGR2RGB))

        if results.multi_face_landmarks:
            for face_landmarks in results.multi_face_landmarks:
                # 左目(130)と右目(359)の座標取得
                left_eye = face_landmarks.landmark[130]
                right_eye = face_landmarks.landmark[359]

                left_x_px, left_y_px = left_eye.x * image_width, left_eye.y * image_height
                right_x_px, right_y_px = right_eye.x * image_width, right_eye.y * image_height

                # 画面上の両目のピクセル距離
                w = math.sqrt((right_x_px - left_x_px)**2 + (right_y_px - left_y_px)**2)
                
                if w == 0:
                    continue

                # Z深度の計算
                z = (W * f) / w

                # X, Y座標の計算 (画面中心を0とする)
                x = ( ((left_x_px + right_x_px)/2) - (image_width / 2) ) * z / f
                y = ( ((left_y_px + right_y_px)/2) - (image_height / 2) ) * z / f

                # Unity(左手座標系: Y上, Z奥)への変換
                # OpenCVの画像座標系はYが下方向なので反転する
                unity_x = x
                unity_y = -y
                unity_z = z

                # UDP送信
                message = f"{unity_x},{unity_y},{unity_z}"
                sock.sendto(message.encode('utf-8'), (UDP_IP, UDP_PORT))

        cv2.imshow('Tracking', image)
        if cv2.waitKey(5) & 0xFF == 27: # ESCキーで終了
            break

cap.release()
cv2.destroyAllWindows()