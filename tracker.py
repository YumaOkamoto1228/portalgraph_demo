import json
import math
import os
import socket
import time

import cv2
import mediapipe as mp

# ===== パラメータ設定 =====
IPD = 0.063             # 現実の瞳孔間距離 (メートル)。自分の値が分かれば書き換える（成人平均 約0.063）
CALIB_DISTANCE = 0.50   # キャリブレーション時に座る、Webカメラから目までの距離 (メートル)
DEFAULT_HFOV = 60.0     # キャリブレーション前に使う、Webカメラの水平画角の仮の値 (度)
# キャリブレーション結果を水平画角に直したとき、この範囲を外れたら警告する (度)
# （C200 は最大でも対角95°。これより広い値は、Cキーを押したときの距離が違っていたことを意味する）
PLAUSIBLE_HFOV = (40.0, 95.0)

CAM_INDEX = 0           # Anker PowerConf C200 (通常はインデックス0か1)
CAP_WIDTH, CAP_HEIGHT, CAP_FPS = 1280, 720, 60   # 解像度より fps を優先した方が遅延が減る
SHOW_PREVIEW = True
PRINT_STATS = True      # 1秒ごとに fps・処理時間をコンソールに表示する

UDP_IP = "127.0.0.1"
UDP_PORT = 5005
CALIB_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "calib.json")

# refine_landmarks=True のときの虹彩中心
LEFT_IRIS, RIGHT_IRIS = 468, 473


def hfov_deg(f, image_width):
    return math.degrees(2 * math.atan((image_width / 2) / f))


def focal_warning(f, image_width):
    """焦点距離がカメラとしてありえない値なら、プレビューに出す警告文を返す（正常なら None）

    f は距離 z の推定だけに効く。f が小さすぎる（画角が広すぎる）と z が実際より短く出て、
    Unity 側で目が画面に近すぎる位置から描画され、箱やキャラが奥へ引き伸ばされて見える。
    """
    hfov = hfov_deg(f, image_width)
    lo, hi = PLAUSIBLE_HFOV
    if lo <= hfov <= hi:
        return None
    print(f"警告: f = {f:.1f}px は水平画角 {hfov:.0f}° に相当し、Webカメラとして不自然です（目安 {lo:.0f}〜{hi:.0f}°）。\n"
          f"      距離の推定がずれて奥行きが不自然に見えます。Webカメラのレンズから目までを"
          f"メジャーで {CALIB_DISTANCE*100:.0f}cm に合わせて、C キーを押し直してください")
    return f"CALIBRATION? HFOV {hfov:.0f}deg is out of range. Sit at {CALIB_DISTANCE*100:.0f}cm and press C"


def load_focal(image_width):
    if os.path.exists(CALIB_FILE):
        with open(CALIB_FILE) as fp:
            data = json.load(fp)
        if data.get("width") == image_width:
            print(f"calib.json から焦点距離を読み込みました: f = {data['f']:.1f}px"
                  f"（水平画角 {hfov_deg(data['f'], image_width):.0f}°）")
            return data["f"]
    f = (image_width / 2) / math.tan(math.radians(DEFAULT_HFOV / 2))
    print(f"未キャリブレーションです（仮の f = {f:.1f}px）。"
          f"Webカメラから {CALIB_DISTANCE*100:.0f}cm の位置に座って C キーを押してください")
    return f


class PerfStats:
    """1秒ごとに fps と処理時間（平均・最大）を集計する"""

    def __init__(self):
        self.summary = None
        self._reset(time.perf_counter())

    def _reset(self, now):
        self.start = now
        self.frames = 0
        self.detected = 0
        self.sums = {"read": 0.0, "infer": 0.0, "proc": 0.0}
        self.maxs = {"read": 0.0, "infer": 0.0, "proc": 0.0}

    def add(self, read_ms, infer_ms, proc_ms, detected):
        self.frames += 1
        self.detected += int(detected)
        for k, v in (("read", read_ms), ("infer", infer_ms), ("proc", proc_ms)):
            self.sums[k] += v
            self.maxs[k] = max(self.maxs[k], v)

        now = time.perf_counter()
        elapsed = now - self.start
        if elapsed >= 1.0:
            n = max(self.frames, 1)
            self.summary = {
                "fps": self.frames / elapsed,
                "detect": 100.0 * self.detected / n,
                **{f"{k}_avg": self.sums[k] / n for k in self.sums},
                **{f"{k}_max": self.maxs[k] for k in self.maxs},
            }
            if PRINT_STATS:
                print(self.text())
            self._reset(now)

    def text(self):
        s = self.summary
        if s is None:
            return "measuring..."
        return (f"{s['fps']:.1f}fps  detect {s['detect']:.0f}%  "
                f"read {s['read_avg']:.1f}  infer {s['infer_avg']:.1f}/{s['infer_max']:.1f}  "
                f"proc {s['proc_avg']:.1f}/{s['proc_max']:.1f} ms (avg/max)")


def save_focal(f, image_width):
    with open(CALIB_FILE, "w") as fp:
        json.dump({"f": f, "width": image_width}, fp)
    print(f"キャリブレーション完了: f = {f:.1f}px（水平画角 {hfov_deg(f, image_width):.0f}°、calib.json に保存）")


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
calib_warning = focal_warning(f, actual_w)

# 計測について
#   read : cap.read() でフレームを待った時間。ほぼ0msが続くなら、処理がカメラより遅くフレームが溜まっている
#   infer: MediaPipe の推論時間
#   proc : フレーム取得完了 → UDP送信までの時間（Unityへも送り、遅延の内訳表示に使う）
#   ※ 撮影（露光）からフレーム取得までのカメラ・ドライバ内の遅延は、ソフトからは測れない
stats = PerfStats()
seq = 0

with mp.solutions.face_mesh.FaceMesh(max_num_faces=1, refine_landmarks=True,
                                     min_detection_confidence=0.5,
                                     min_tracking_confidence=0.5) as face_mesh:
    while cap.isOpened():
        t_wait = time.perf_counter()
        success, image = cap.read()
        t_read = time.perf_counter()
        if not success:
            continue

        h_img, w_img, _ = image.shape
        rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        rgb.flags.writeable = False
        t_infer = time.perf_counter()
        results = face_mesh.process(rgb)
        infer_ms = (time.perf_counter() - t_infer) * 1000

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
                # 4番目: フレーム取得から送信までの処理時間(ms)、5番目: 通し番号（Unity側でパケットロスを数える）
                proc_ms = (time.perf_counter() - t_read) * 1000
                message = f"{-x:.5f},{-y:.5f},{z:.5f},{proc_ms:.2f},{seq}"
                sock.sendto(message.encode("utf-8"), (UDP_IP, UDP_PORT))
                seq += 1

        stats.add((t_read - t_wait) * 1000, infer_ms,
                  (time.perf_counter() - t_read) * 1000, eye_px is not None)

        if SHOW_PREVIEW:
            preview = cv2.flip(image, 1)  # 鏡像で表示した方が直感的
            if eye_px is not None:
                cv2.circle(preview, (int(w_img - eye_px[0]), int(eye_px[1])), 5, (0, 255, 0), -1)
                cv2.putText(preview, f"x={-x:+.3f} y={-y:+.3f} z={z:.3f} m", (10, 30),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 255, 0), 2)
            cv2.putText(preview, stats.text(), (10, 60),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
            if calib_warning:
                cv2.putText(preview, calib_warning, (10, 90),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 255), 2)
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
            calib_warning = focal_warning(f, w_img)

cap.release()
cv2.destroyAllWindows()
