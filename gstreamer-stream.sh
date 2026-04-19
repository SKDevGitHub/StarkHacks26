gst-launch-1.0 v4l2src device=$1 ! \
 video/x-raw,format=YUY2,width=640,height=480,framerate=30/1 ! \
 videoconvert ! \
 x264enc tune=zerolatency bitrate=500 speed-preset=ultrafast ! \
 rtph264pay config-interval=1 pt=96 ! \
 udpsink host=$2 port=5000
