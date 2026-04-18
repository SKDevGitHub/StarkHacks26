import cv2
import time

camera_ids = [0, 1, 2]
caps = []

for i in camera_ids:
    cap = cv2.VideoCapture(i, cv2.CAP_AVFOUNDATION)
    time.sleep(1)  # 👈 important
    if cap.isOpened():
        print(f"Camera {i} opened")
        caps.append(cap)
    else:
        print(f"Camera {i} failed to open")

while True:
    for idx, cap in enumerate(caps):
        ret, frame = cap.read()
        if not ret:
            print(f"Camera {idx} read failed")
            continue

        cv2.imshow(f"Camera {idx}", frame)

    if cv2.waitKey(1) & 0xFF == 27:
        break

for cap in caps:
    cap.release()

cv2.destroyAllWindows()