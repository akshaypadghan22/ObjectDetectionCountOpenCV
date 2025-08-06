using OpenCvSharp;
using OpenCvSharp.Dnn;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ObjectDetectionApp
{
    public partial class Form1 : Form
    {
        private VideoCapture capture;
        private Thread cameraThread;
        private volatile bool isCameraRunning = false; // Use volatile for thread safety
        private Net net;
        private string[] classNames;
        private readonly object frameLock = new object(); // For thread-safe frame updates

        public Form1()
        {
            InitializeComponent();
            LoadModel();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            StartCamera();
        }

        private void LoadModel()
        {
            try
            {
                string modelPath = Path.Combine(Application.StartupPath, "model");

                // Step 1: Validate the 'model' directory exists
                if (!Directory.Exists(modelPath))
                {
                    MessageBox.Show("The 'model' directory does not exist.");
                    Close();
                    return; 
                }

                // Step 2: Define and validate each file one by one
                string cfgPath = Path.Combine(modelPath, "yolov3.cfg");
                if (!File.Exists(cfgPath))
                {
                    MessageBox.Show("The file 'yolov3.cfg' is missing in the 'model' directory.");
                    Close();
                    return;
                }

                string weightsPath = Path.Combine(modelPath, "yolov3.weights");
                if (!File.Exists(weightsPath))
                {
                    MessageBox.Show("The file 'yolov3.weights' is missing in the 'model' directory.");
                    Close();
                    return;
                }

                string namesPath = Path.Combine(modelPath, "coco.names");
                if (!File.Exists(namesPath))
                {
                    MessageBox.Show("The file 'coco.names' is missing in the 'model' directory.");
                    Close();
                    return;
                }

                // Step 3: Load the model and class names if all files are present
                net = CvDnn.ReadNetFromDarknet(cfgPath, weightsPath);
                net.SetPreferableBackend(Backend.OPENCV);
                net.SetPreferableTarget(Target.CPU);

                classNames = File.ReadAllLines(namesPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load model: {ex.Message}");
                Close();
            }
        }

        private void StartCamera()
        {
            try
            {
                capture = new VideoCapture(0);
                if (!capture.IsOpened())
                {
                    MessageBox.Show("Camera not accessible.");
                    return;
                }

                isCameraRunning = true;
                cameraThread = new Thread(ProcessCamera)
                {
                    IsBackground = true
                };
                cameraThread.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start camera: {ex.Message}");
            }
        }

        private void ProcessCamera()
        {
            while (isCameraRunning)
            {
                using (var frame = new Mat())
                {
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        Thread.Sleep(10); // Prevent tight loop on failure
                        continue;
                    }

                    DetectAndDraw(frame);

                    lock (frameLock)
                    {
                        using (Bitmap image = BitmapConverter.ToBitmap(frame))
                        {
                            pictureBox1.Invoke(new Action(() =>
                            {
                                if (pictureBox1.Image != null)
                                {
                                    pictureBox1.Image.Dispose(); // Dispose previous image
                                }
                                pictureBox1.Image = new Bitmap(image); // Create a copy to avoid cross-thread issues
                            }));
                        }
                    }
                }
            }
        }

        private void DetectAndDraw(Mat frame)
        {
            try
            {
                int frameWidth = frame.Width;
                int frameHeight = frame.Height;

                // Preprocess image
                using (var blob = CvDnn.BlobFromImage(frame, 1 / 255.0, new OpenCvSharp.Size(416, 416), new Scalar(), swapRB: true, crop: false))
                {
                    net.SetInput(blob);

                    // Get output layers
                    var outputNames = net.GetUnconnectedOutLayersNames();
                    using (var outputBlobs = new MatArray(outputNames.Length))
                    {
                        net.Forward(outputBlobs.Mats, outputNames);

                        var classIds = new List<int>();
                        var confidences = new List<float>();
                        var boxes = new List<Rect>();

                        // Process each output layer
                        foreach (var prob in outputBlobs.Mats)
                        {
                            for (int i = 0; i < prob.Rows; i++)
                            {
                                // Access data directly instead of copying to array
                                float confidence = prob.At<float>(i, 4);
                                if (confidence < 0.5f) continue;

                                // Find max class score
                                int classId = -1;
                                float maxScore = 0;
                                for (int j = 0; j < classNames.Length; j++)
                                {
                                    float score = prob.At<float>(i, 5 + j);
                                    if (score > maxScore)
                                    {
                                        maxScore = score;
                                        classId = j;
                                    }
                                }

                                if (maxScore > 0.5f)
                                {
                                    float centerX = prob.At<float>(i, 0) * frameWidth;
                                    float centerY = prob.At<float>(i, 1) * frameHeight;
                                    float width = prob.At<float>(i, 2) * frameWidth;
                                    float height = prob.At<float>(i, 3) * frameHeight;

                                    int left = (int)(centerX - width / 2);
                                    int top = (int)(centerY - height / 2);

                                    classIds.Add(classId);
                                    confidences.Add(maxScore);
                                    boxes.Add(new Rect(left, top, (int)width, (int)height));
                                }
                            }
                            prob.Dispose(); // Dispose each output Mat
                        }

                        // Apply Non-Max Suppression
                        int[] indices;
                        CvDnn.NMSBoxes(boxes.ToArray(), confidences.ToArray(), 0.5f, 0.4f, out indices);

                        int count = 0;
                        foreach (var i in indices)
                        {
                            Rect box = boxes[i];
                            string label = $"{classNames[classIds[i]]}: {confidences[i]:0.00}";
                            Cv2.Rectangle(frame, box, Scalar.Red, 2);
                            Cv2.PutText(frame, label, new OpenCvSharp.Point(box.X, box.Y - 5), HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 2);
                            count++;
                        }

                        Cv2.PutText(frame, $"Objects Detected: {count}", new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 1, Scalar.LimeGreen, 2);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error (in a real application, use a proper logging framework)
                Console.WriteLine($"Detection error: {ex.Message}");
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            isCameraRunning = false;
            cameraThread?.Join(1000); // Wait up to 1 second for thread to terminate
            capture?.Release();
            capture?.Dispose();
            net?.Dispose();
            pictureBox1?.Image?.Dispose();
        }

        // Helper class to manage Mat array for output blobs
        private class MatArray : IDisposable
        {
            public Mat[] Mats { get; }

            public MatArray(int size)
            {
                Mats = new Mat[size];
                for (int i = 0; i < size; i++)
                {
                    Mats[i] = new Mat();
                }
            }

            public void Dispose()
            {
                foreach (var mat in Mats)
                {
                    mat?.Dispose();
                }
            }
        }
    }
}