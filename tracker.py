import json
import math
import os
import socket

import cv2
import mediapipe as mp

# ===== パラメータ設定 =====
IPD = 0.063             # 現実の瞳孔間距離 (メートル)。自分の値が分かれば書き換える（成人平均 約0.063）
CALIB_DISTANCE = 0.50   # キャリブレーション時に座る、Webカメラから目までの距離 (メートル)
DEFAULT_HFOV = 60.0     # キャリブレーション前に使う、Webカメラの水平画角の仮の値 (度)

CAM_INDEX = 0           # Anker PowerConf C200 (通常はインデックス0か1)
CAP_WIDTH, CAP_HEIGHT, CAP_FPS = 1280, 720, 60   # 解像度より fps を優先した方が遅延が減る
SHOW_PREVIEW = True

UDP_IP = "127.0.0.1"
UDP_PORT = 5005
CALIB_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "calib.json")

# refine_landmarks=True のときの虹彩中心
LEFT_IRIS, RIGHT_IRIS = 468, 473


def load_focal(image_width):
    if os.path.exists(CALIB_FILE):
        with open(CALIB_FILE) as fp:
            data = json.load(fp)
        if data.get("width") == image_width:
            print(f"calib.json から焦点距離を読み込みました: f = {data['f']:.1f}px")
            return data["f"]
    f = (image_width / 2) / math.tan(math.radians(DEFAULT_HFOV / 2))
    print(f"未キャリブレーションです（仮の f = {f:.1f}px）。"
          f"Webカメラから {CALIB_DISTANCE*100:.0f}cm の位置に座って C キーを押してください")
    return f


def save_focal(f, image_width):
    with open(CALIB_FILE, "w") as fp:
        json.dump({"f": f, "width": image_width}, fp)
    print(f"キャリブレーション完了: f = {f:.1f}px（calib.json に保存）")


sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

# DirectShow + MJPG にすると、Windows で高fps・低遅延で取り込める
cap = cv2.VideoCapture(CAM_INDEX, cv2.CAP_DSHOW)
cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
cap.set(cv2.CAP_PROP_FRAME_WIDTH, CAP_WIDTH)
cap.set(cv2.CAP_PROP_FRAME_HEIGHT, CAP_HEIGHT)
cap.set(cv2.CAP_PROP_FPS, CAP_FPS)
cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)

# 設定が反映されたか確認（カメラによっては最大値が制限されるため）
actual_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
actual_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
print(f"Resolution: {actual_w}x{actual_h} @ {cap.get(cv2.CAP_PROP_FPS):.0f}fps")

f = load_focal(actual_w)

with mp.solutions.face_mesh.FaceMesh(max_num_faces=1, refine_landmarks=True,
                                     min_detection_confidence=0.5,
                                     min_tracking_confidence=0.5) as face_mesh:
    while cap.isOpened():
        success, image = cap.read()
        if not success:
            continue

        h_img, w_img, _ = image.shape
        rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        rgb.flags.writeable = False
        results = face_mesh.process(rgb)

        eye_px = None
        iris_px = 0.0
        if results.multi_face_landmarks:
            lm = results.multi_face_landmarks[0].landmark
            l, r = lm[LEFT_IRIS], lm[RIGHT_IRIS]

            # 両目の間隔をランドマークの z も含めた3次元で測る
            # → 顔を左右に向けても見かけの間隔が縮みにくく、距離が暴れない
            iris_px = math.sqrt(((r.x - l.x) * w_img) ** 2 +
                                ((r.y - l.y) * h_img) ** 2 +
                                ((r.z - l.z) * w_img) ** 2)

            if iris_px > 0:
                eye_px = ((l.x + r.x) / 2 * w_img, (l.y + r.y) / 2 * h_img)

                # 距離（ピンホールカメラモデル）
                z = IPD * f / iris_px
                # 画像中心からのずれをメートルに変換
                x = (eye_px[0] - w_img / 2) * z / f
                y = (eye_px[1] - h_img / 2) * z / f

                # 送信する座標: x=見ている人の右が+、y=上が+、z=Webカメラからの距離
                # （画像は左右反転していないので、人が右へ動くと画像上では左へ動く → x を反転）
                message = f"{-x:.5f},{-y:.5f},{z:.5f}"
                sock.sendto(message.encode("utf-8"), (UDP_IP, UDP_PORT))

        if SHOW_PREVIEW:
            preview = cv2.flip(image, 1)  # 鏡像で表示した方が直感的
            if eye_px is not None:
                cv2.circle(preview, (int(w_img - eye_px[0]), int(eye_px[1])), 5, (0, 255, 0), -1)
                cv2.putText(preview, f"x={-x:+.3f} y={-y:+.3f} z={z:.3f} m", (10, 30),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 255, 0), 2)
            cv2.putText(preview, f"C: calibrate at {CALIB_DISTANCE*100:.0f}cm   ESC: quit",
                        (10, h_img - 15), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
            cv2.imshow("Tracking", preview)

        key = cv2.waitKey(1) & 0xFF
        if key == 27:  # ESCキーで終了
            break
        if key in (ord("c"), ord("C")) and iris_px > 0:
            # 既知の距離に座った状態での見かけの両目間隔から焦点距離を逆算
            f = iris_px * CALIB_DISTANCE / IPD
            save_focal(f, w_img)

cap.release()
cv2.destroyAllWindows()
