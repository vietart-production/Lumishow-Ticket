using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

/// <summary>
/// 3 trạng thái quét, tương ứng 3 màu khung quanh QR:
/// Processing = vàng (đang xác thực), Valid = xanh (hợp lệ), Invalid = đỏ (không hợp lệ).
/// </summary>
public enum ScanState
{
    Processing,
    Valid,
    Invalid
}

public class CameraScanner : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("RawImage sẽ hiển thị hình ảnh camera")]
    [SerializeField] private RawImage rawImage;

    [Tooltip("Bật nếu RawImage dùng anchor kéo giãn theo parent (anchorMin/anchorMax khác nhau - " +
             "kiểu phủ kín khung UI). Tắt nếu RawImage dùng 1 anchor điểm cố định " +
             "(anchorMin = anchorMax, ví dụ neo giữa). Cần đúng để tính sizeDelta chính xác " +
             "trong ApplyCameraOrientation().")]
    [SerializeField] private bool rawImageUsesStretchedAnchors = true;

    [Header("Camera Settings")]
    [SerializeField] private int requestedWidth = 1920;
    [SerializeField] private int requestedHeight = 1080;
    [SerializeField] private int requestedFPS = 30;
    [Tooltip("Ưu tiên camera sau (rear) nếu máy có nhiều camera")]
    [SerializeField] private bool preferRearCamera = true;

    [Header("Test Mode (Editor - KHÔNG bật khi build thật)")]
    [Tooltip("BẬT để test luồng quét/checkin trong Unity Editor MÀ KHÔNG CẦN webcam thật hoạt động " +
             "(vd khi gặp lỗi webcam kiểu 'Could not connect pins' trên PC). Camera chỉ định bên " +
             "dưới (testCamera, hoặc Camera.main nếu để trống) sẽ render THẲNG ra Display 1 như bình " +
             "thường, và app quét QR bằng cách chụp lại màn hình mỗi lượt decode (ScreenCapture) - " +
             "khác với việc dùng RawImage/RenderTexture, nên vé giả có thể là bất kỳ thứ gì hiện trên " +
             "Display 1 (vật thể trong Scene, hoặc cả 1 QR hiển thị qua UI/Canvas). Khi phát hiện QR, " +
             "chạy ĐÚNG luồng thật (OnQRCodeScanned -> TicketManager -> FirebaseManager.CheckInTicket) " +
             "- không phải giả lập kết quả. Tắt (mặc định) khi build lên máy thật.")]
    [SerializeField] private bool testModeUseMainCamera = false;

    [Tooltip("Camera dùng để test (sẽ bị chuyển sang render ra Display 1) - để trống sẽ tự lấy " +
             "Camera.main.")]
    [SerializeField] private Camera testCamera;

    [Header("QR Box")]
    [Tooltip("Prefab hiển thị khung quanh QR (vd Assets/Prefabs/BoxQR.prefab) - gồm 1 RectTransform " +
             "gốc (anchorMin = anchorMax = pivot = (0.5, 0.5)) + các Image con là hình 4 góc khung. " +
             "QRBoxUI sẽ Instantiate prefab này rồi tự dịch chuyển/xoay/scale nguyên khối theo QR " +
             "thật mỗi frame - xem QRBoxUI class doc.")]
    public GameObject cornerBoxPrefab;

    [Tooltip("Bật nếu khung/label bị lộn ngược khi test trên máy thật")]
    [SerializeField] private bool invertVerticalMapping = false;
    [SerializeField] private Color colorProcessing = new Color(1f, 0.85f, 0f);    // vàng
    [SerializeField] private Color colorValid = new Color(0.2f, 0.85f, 0.3f);     // xanh
    [SerializeField] private Color colorInvalid = new Color(0.95f, 0.25f, 0.25f); // đỏ
    [Tooltip("Số frame liên tiếp không thấy QR trước khi coi như QR đã rời khỏi khung hình")]
    [SerializeField] private int maxMissFrames = 5;

    [Header("QR Box - Phát hiện sớm (tuỳ chọn)")]
    [Tooltip("Bật để khung hiện lên NGAY khi ZXing tìm thấy hình QR trong khung hình (đủ finder " +
             "pattern), KỂ CẢ khi chưa đọc được nội dung bên trong (do mờ/xa/nghiêng...) - tức khung " +
             "chỉ cần QR nằm trong camera, không cần qua bước decode/validate nào. Khung ở trạng thái " +
             "này là hình chữ nhật ƯỚC LƯỢNG (bounding box thẳng, không xoay đúng góc QR thật) vì ZXing " +
             "chưa trả Result nên chưa có đủ thông tin để tính khung chính xác như UpdateBoxCorners(). " +
             "Ngay khi đọc được nội dung QR, khung sẽ tự chuyển sang khung chính xác (có xoay đúng góc) " +
             "như bình thường. Tắt (mặc định) thì khung chỉ hiện khi đã đọc được nội dung QR.")]
    [SerializeField] private bool showBoxOnDetectionOnly = false;

    [Tooltip("Số lượt decode liên tiếp không thấy điểm QR nào trước khi ẩn khung 'đang dò tìm' " +
             "(chỉ áp dụng khi showBoxOnDetectionOnly = true).")]
    [SerializeField] private int maxDetectionMissFrames = 3;

    [Tooltip("Màu khung + label khi mới chỉ PHÁT HIỆN được hình QR, chưa đọc được nội dung " +
             "(chỉ áp dụng khi showBoxOnDetectionOnly = true).")]
    [SerializeField] private Color colorDetecting = new Color(0.4f, 0.7f, 1f); // xanh dương nhạt

    [Header("Camera Warm-up")]
    [Tooltip("Số frame đầu tiên sẽ bị ẩn đi trước khi hiện camera lên UI, " +
             "vì vài frame đầu trên nhiều máy Android thường bị mờ/méo/sai kích thước tạm thời")]
    [SerializeField] private int requiredWarmUpFrames = 8;

    [Header("Performance")]
    [Tooltip("Khoảng cách tối thiểu (giây) giữa 2 lần decode QR. Không cần decode mỗi frame " +
             "vì mắt người không phân biệt được, trong khi decode lại khá tốn CPU (nhất là với TryHarder=true).")]
    [SerializeField] private float decodeInterval = 0.15f;

    private WebCamTexture cameraTexture;
    private BarcodeReader barcodeReader;
    private QRBoxUI qrBox;
    private Text cameraStatusText;

    // Test Mode: nguồn ảnh thay thế WebCamTexture, xem StartTestMode()/CaptureScreenAndDecode().
    private Camera activeTestCamera;
    private int testCameraOriginalTargetDisplay;
    private RenderTexture testCameraOriginalTargetTexture;
    private bool testModeReady;
    private bool isCapturingTestScreenshot;
    private int testFrameWidth;
    private int testFrameHeight;

    /// <summary>
    /// Kích thước khung hình hiện tại - ở Test Mode là kích thước ảnh chụp màn hình gần nhất
    /// (testFrameWidth/Height, xem CaptureScreenAndDecode(), fallback về Screen.width/height nếu
    /// chưa chụp lần nào), hoặc cameraTexture.width/height khi dùng webcam thật. Mọi phép tính toạ
    /// độ (UnrotateResultPoint, RawPixelToLocal, PixelToLocalRaw) dùng property này thay vì đọc
    /// thẳng cameraTexture để dùng chung được cho cả 2 nguồn ảnh.
    /// </summary>
    private int FrameWidth => testModeUseMainCamera ? (testFrameWidth > 0 ? testFrameWidth : Screen.width) : cameraTexture.width;
    private int FrameHeight => testModeUseMainCamera ? (testFrameHeight > 0 ? testFrameHeight : Screen.height) : cameraTexture.height;

    private bool isWarmedUp = false;
    private int warmUpFrameCount = 0;
    private bool isPermissionFlowRunning;
    private string currentQRValue;
    private ScanState currentState;
    private int missFrameCount;

    // Buffer pixel dùng lại mỗi frame thay vì để GetPixels32() cấp phát mảng mới liên tục
    // (mảng ~vài MB mỗi lần với ảnh 720p -> nếu cấp phát mỗi frame sẽ gây GC spike, giật hình theo chu kỳ).
    private Color32[] pixelBuffer;
    private int pixelBufferWidth;
    private int pixelBufferHeight;
    private float timeSinceLastDecode;

    // Decode QR chạy ở thread nền để không chặn main thread (chặn main thread = giật camera preview).
    private volatile bool isDecoding = false;
    private volatile bool hasPendingResult = false;
    private Result pendingResult;
    private readonly object resultLock = new object();

    // Dùng cho showBoxOnDetectionOnly: ZXing gọi ResultPointCallback trên thread nền (trong lúc
    // Decode() đang chạy) mỗi khi tìm thấy 1 điểm có khả năng là finder pattern, kể cả khi cuối
    // cùng KHÔNG decode được nội dung. detectedPointsBuffer gom các điểm đó cho 1 lượt decode;
    // pendingDetectionPoints là bản snapshot được Update() (main thread) đọc ra mỗi lượt.
    private readonly List<ResultPoint> detectedPointsBuffer = new List<ResultPoint>();
    private readonly object detectionLock = new object();
    private List<ResultPoint> pendingDetectionPoints;
    private int detectionMissCount;
    private bool isPreviewBoxShowing;

    /// <summary>
    /// Bắn ra mỗi khi phát hiện 1 QR code MỚI (khác với QR đang track).
    /// Các script khác (vd TicketManager) subscribe vào đây để xử lý mã vé.
    /// </summary>
    public event System.Action<string> OnQRCodeScanned;

    private void Start()
    {
        if (rawImage == null)
        {
            Debug.LogError("CameraScanner: Chưa gán RawImage trong Inspector!");
            return;
        }

        SetupAspectFitter();

        qrBox = QRBoxUI.Create(rawImage.rectTransform, cornerBoxPrefab);
        cameraStatusText = BuildCameraStatusLabel();

        barcodeReader = new BarcodeReader
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat>
                {
                    BarcodeFormat.QR_CODE
                }
            }
        };

        if (showBoxOnDetectionOnly)
        {
            // Yêu cầu ZXing gọi OnPossibleResultPointFound() ngay khi tìm thấy finder pattern,
            // kể cả khi decode thất bại - dùng để hiện khung "đang dò tìm" sớm hơn.
            barcodeReader.Options.Hints[DecodeHintType.NEED_RESULT_POINT_CALLBACK] =
                new ResultPointCallback(OnPossibleResultPointFound);
        }

        if (testModeUseMainCamera)
        {
            StartTestMode();
        }
        else
        {
            StartCoroutine(RequestCameraPermissionThenStart());
        }
    }

    /// <summary>
    /// Test Mode: thay vì WebCamTexture, để 1 Camera trong Scene (testCamera, hoặc Camera.main nếu
    /// để trống) render THẲNG ra Display 1 như bình thường (targetTexture = null, targetDisplay =
    /// 0) - không qua RenderTexture trung gian, nên không có rủi ro "2 gương đối diện nhau" (camera
    /// tự render lại chính output của nó qua 1 RawImage đang hiển thị RenderTexture đó). Lấy pixel
    /// để decode QR bằng cách chụp lại màn hình sau khi frame đã render xong (xem
    /// CaptureScreenAndDecode) thay vì đọc từ RenderTexture. rawImage không cần hiển thị gì nữa vì
    /// camera đã tự vẽ ra màn hình - chỉ tắt phần hiển thị (enabled), giữ nguyên GameObject active
    /// vì QRBoxUI (khung quét) được tạo làm con của rawImage.transform.
    /// </summary>
    private void StartTestMode()
    {
        Camera cam = testCamera != null ? testCamera : Camera.main;

        if (cam == null)
        {
            Debug.LogError("CameraScanner (Test Mode): Không tìm thấy Camera nào để dùng làm nguồn " +
                            "ảnh - gán Test Camera trong Inspector hoặc đảm bảo Scene có Camera.main.");
            SetCameraStatus("Test Mode: không tìm thấy Camera để dùng làm nguồn ảnh QR.");
            return;
        }

        activeTestCamera = cam;
        testCameraOriginalTargetDisplay = cam.targetDisplay;
        testCameraOriginalTargetTexture = cam.targetTexture;

        cam.targetTexture = null;
        cam.targetDisplay = 0; // Display 1

        if (rawImage != null)
        {
            rawImage.enabled = false;
        }

        testModeReady = true;

        Debug.Log("CameraScanner: Test Mode BẬT - dùng Camera '" + cam.name +
                   "' render thẳng ra Display 1, quét QR bằng ScreenCapture.");
    }

    /// <summary>
    /// Xin quyền Camera TƯỜNG MINH trước khi tạo WebCamTexture, thay vì gọi thẳng StartCamera()
    /// và mặc định hệ điều hành sẽ tự lo. Trước đây nếu người dùng từ chối quyền (hoặc thu hồi
    /// quyền sau đó trong Cài đặt máy), màn hình chỉ đứng im màu đen VĨNH VIỄN mà không có thông
    /// báo gì cho nhân viên tại cổng, vì Update() chỉ âm thầm chờ cameraTexture.width >= 100 -
    /// điều kiện không bao giờ đạt được khi không có quyền. Giờ trạng thái luôn hiện rõ qua
    /// cameraStatusText để nhân viên biết cần vào Cài đặt cấp lại quyền thay vì tưởng app bị treo.
    /// </summary>
    private IEnumerator RequestCameraPermissionThenStart()
    {
        if (isPermissionFlowRunning)
            yield break;

        isPermissionFlowRunning = true;
        SetCameraStatus("Đang khởi động camera...");

        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        }

        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogError("CameraScanner: Người dùng chưa cấp quyền Camera.");
            SetCameraStatus("Không có quyền truy cập Camera.\nVui lòng cấp quyền trong Cài đặt máy rồi quay lại app.");
            isPermissionFlowRunning = false;
            yield break;
        }

        StartCamera();
        isPermissionFlowRunning = false;
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus || testModeUseMainCamera || cameraTexture != null)
            return;

        StartCoroutine(RequestCameraPermissionThenStart());
    }

    private void SetupAspectFitter()
    {
        if (rawImage == null)
        {
            Debug.LogError("CameraScanner: Chưa gán RawImage trong Inspector!");
            return;
        }

        // KHÔNG dùng Unity AspectRatioFitter nữa (kể cả mode EnvelopeParent), vì component này
        // tính sizeDelta dựa trên rect của parent mà KHÔNG biết RawImage sẽ bị xoay theo
        // videoRotationAngle sau đó -> với góc xoay 90/270 độ nó luôn tính sai (phủ/zoom quá mức).
        // Đây là hạn chế đã biết của AspectRatioFitter, không phải chỉ riêng project này gặp.
        // Xem ApplyCameraOrientation() - kích thước "cover" giờ được tự tính có xét tới xoay.
        AspectRatioFitter existingFitter = rawImage.GetComponent<AspectRatioFitter>();
        if (existingFitter != null)
        {
            existingFitter.enabled = false;
        }
    }

    private void StartCamera()
    {
        WebCamDevice[] devices = WebCamTexture.devices;

        if (devices.Length == 0)
        {
            Debug.LogError("Không tìm thấy camera!");
            SetCameraStatus("Không tìm thấy camera trên thiết bị này.");
            return;
        }

        int deviceIndex = 0;

        if (preferRearCamera)
        {
            for (int i = 0; i < devices.Length; i++)
            {
                if (!devices[i].isFrontFacing)
                {
                    deviceIndex = i;
                    break;
                }
            }
        }

        string cameraName = devices[deviceIndex].name;

        cameraTexture = new WebCamTexture(
            cameraName,
            requestedWidth,
            requestedHeight,
            requestedFPS
        );

        if (rawImage != null)
        {
            // Đây chính là "target texture" mà RawImage sẽ hiển thị.
            rawImage.texture = cameraTexture;

            // Ẩn RawImage cho tới khi camera "ấm" (đủ số frame ổn định),
            // tránh lộ ra vài frame đầu bị mờ/méo/sai tỉ lệ.
            rawImage.enabled = false;
        }

        cameraTexture.Play();

        isWarmedUp = false;
        warmUpFrameCount = 0;

        Debug.Log("Camera started: " + cameraName);
    }

    private void Update()
    {
        if (testModeUseMainCamera)
        {
            if (!testModeReady)
                return; // StartTestMode() thất bại (không tìm thấy Camera) - xem log lúc Start().

            UpdateTestMode();
        }
        else
        {
            UpdateDeviceCamera();
        }
    }

    private void UpdateDeviceCamera()
    {
        if (cameraTexture == null || !cameraTexture.isPlaying)
            return;

        if (cameraTexture.width < 100)
            return;

        // Set lại xoay/lật/tỉ lệ MỖI frame (rất rẻ: chỉ set vài property) thay vì
        // chỉ 1 lần duy nhất, vì nhiều máy Android trả width/height/rotation "tạm"
        // ở vài frame đầu rồi mới chốt giá trị thật -> khoá cứng 1 lần dễ bị méo vĩnh viễn.
        ApplyCameraOrientation();

        if (!isWarmedUp)
        {
            warmUpFrameCount++;

            if (warmUpFrameCount < requiredWarmUpFrames)
            {
                // Chưa đủ "ấm" -> bỏ qua frame này, không hiện lên UI, không quét QR.
                return;
            }

            isWarmedUp = true;

            if (rawImage != null)
            {
                rawImage.enabled = true;
            }

            SetCameraStatus(null);
        }

        ProcessPendingDecodeResult();

        // Throttle: không cần decode mỗi frame, và không khởi chạy lượt decode mới
        // nếu lượt trước vẫn đang chạy nền.
        timeSinceLastDecode += Time.deltaTime;
        if (timeSinceLastDecode < decodeInterval || isDecoding)
            return;
        timeSinceLastDecode = 0f;

        int width = cameraTexture.width;
        int height = cameraTexture.height;

        if (pixelBuffer == null || pixelBufferWidth != width || pixelBufferHeight != height)
        {
            pixelBuffer = new Color32[width * height];
            pixelBufferWidth = width;
            pixelBufferHeight = height;
        }

        // Tái dùng buffer thay vì để GetPixels32() tự cấp phát mảng mới mỗi lần.
        cameraTexture.GetPixels32(pixelBuffer);

        // Clone riêng cho thread nền: cameraTexture sẽ tiếp tục ghi đè pixelBuffer ở các frame
        // sau trong khi thread nền còn đang decode, nên không thể share thẳng pixelBuffer.
        Color32[] snapshot = (Color32[])pixelBuffer.Clone();
        RunDecodeAsync(snapshot, width, height);
    }

    /// <summary>
    /// Test Mode: giống UpdateDeviceCamera() nhưng không có warm-up/permission, và nguồn ảnh lấy
    /// bằng cách chụp lại màn hình (CaptureScreenAndDecode) thay vì WebCamTexture.
    /// </summary>
    private void UpdateTestMode()
    {
        ProcessPendingDecodeResult();

        timeSinceLastDecode += Time.deltaTime;
        if (timeSinceLastDecode < decodeInterval || isDecoding || isCapturingTestScreenshot)
            return;
        timeSinceLastDecode = 0f;

        StartCoroutine(CaptureScreenAndDecode());
    }

    /// <summary>
    /// Chụp lại đúng những gì đang thật sự hiển thị trên Display 1 (sau khi frame đã render xong -
    /// yield WaitForEndOfFrame) làm nguồn decode QR. Đơn giản và an toàn hơn hẳn đọc lại từ
    /// RenderTexture của chính camera: đây chỉ là 1 lần chụp ảnh SAU KHI render xong, không phải
    /// camera tự nhìn lại chính output của nó, nên không có rủi ro vòng lặp phản chiếu.
    /// </summary>
    private IEnumerator CaptureScreenAndDecode()
    {
        isCapturingTestScreenshot = true;

        yield return new WaitForEndOfFrame();

        Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
        testFrameWidth = shot.width;
        testFrameHeight = shot.height;

        Color32[] snapshot = shot.GetPixels32();
        Destroy(shot);

        isCapturingTestScreenshot = false;
        RunDecodeAsync(snapshot, testFrameWidth, testFrameHeight);
    }

    /// <summary>
    /// Xử lý kết quả của lượt decode trước đó (chạy nền) nếu đã xong - dùng chung cho cả
    /// UpdateDeviceCamera() và UpdateTestMode(). Vì decode chạy bất đồng bộ nên có thể trễ vài
    /// frame so với lúc khởi chạy, nhưng không sao vì QR không di chuyển nhanh tới mức lệch vài
    /// chục ms là hỏng UX.
    /// </summary>
    private void ProcessPendingDecodeResult()
    {
        if (!hasPendingResult)
            return;

        Result finishedResult;
        lock (resultLock)
        {
            finishedResult = pendingResult;
            pendingResult = null;
            hasPendingResult = false;
        }

        List<ResultPoint> finishedDetectionPoints = null;
        if (showBoxOnDetectionOnly)
        {
            lock (detectionLock)
            {
                finishedDetectionPoints = pendingDetectionPoints;
                pendingDetectionPoints = null;
            }
        }

        HandleDecodeResult(finishedResult, finishedDetectionPoints);
    }

    /// <summary>
    /// Khởi chạy 1 lượt decode QR trên thread nền cho 1 snapshot pixel - dùng chung cho cả
    /// UpdateDeviceCamera() và UpdateTestMode(), khác nhau duy nhất ở nguồn snapshot.
    /// </summary>
    private void RunDecodeAsync(Color32[] snapshot, int width, int height)
    {
        isDecoding = true;

        if (showBoxOnDetectionOnly)
        {
            // Xoá điểm của lượt decode trước, để OnPossibleResultPointFound() gom điểm mới
            // sạch sẽ cho đúng lượt Decode() sắp chạy.
            lock (detectionLock)
            {
                detectedPointsBuffer.Clear();
            }
        }

        Task.Run(() =>
        {
            Result r = null;
            try
            {
                r = barcodeReader.Decode(snapshot, width, height);
            }
            catch (System.Exception e)
            {
                Debug.LogError("Lỗi khi decode QR: " + e.Message);
            }
            finally
            {
                if (showBoxOnDetectionOnly)
                {
                    lock (detectionLock)
                    {
                        pendingDetectionPoints = new List<ResultPoint>(detectedPointsBuffer);
                    }
                }

                lock (resultLock)
                {
                    pendingResult = r;
                    hasPendingResult = true;
                }
                isDecoding = false;
            }
        });
    }

    /// <summary>
    /// Xử lý kết quả của 1 lượt decode (thành công hoặc không thấy QR).
    /// Tách riêng vì decode giờ chạy bất đồng bộ, kết quả có thể đến ở 1 frame khác
    /// so với frame đã khởi chạy lượt decode đó.
    /// </summary>
    private void HandleDecodeResult(Result result, List<ResultPoint> detectionPoints)
    {
        if (result != null && result.ResultPoints != null && result.ResultPoints.Length >= 3)
        {
            missFrameCount = 0;
            detectionMissCount = 0;
            isPreviewBoxShowing = false;

            if (result.Text != currentQRValue)
            {
                // QR mới xuất hiện trong khung hình -> bắt đầu 1 lượt xác thực mới.
                currentQRValue = result.Text;
                currentState = ScanState.Processing;

                qrBox.SetVisible(true);
                qrBox.SetColor(colorProcessing);
                qrBox.SetLabel("Đang kiểm tra...");

                OnQRCodeDetected(currentQRValue);
            }

            UpdateBoxCorners(result);
        }
        else
        {
            missFrameCount++;

            // Chưa đọc được nội dung QR ở lượt này. Nếu bật showBoxOnDetectionOnly, ZXing có tìm
            // thấy finder pattern (detectionPoints có điểm) VÀ không đang track 1 QR đã decode
            // được (currentQRValue == null, để không đè lên khung chính xác đang hiển thị) thì
            // hiện tạm khung ước lượng - chỉ cần QR nằm trong khung hình, không cần decode xong.
            if (showBoxOnDetectionOnly && currentQRValue == null &&
                detectionPoints != null && detectionPoints.Count > 0)
            {
                detectionMissCount = 0;
                ShowDetectionPreviewBox(detectionPoints);
                return;
            }

            if (showBoxOnDetectionOnly && isPreviewBoxShowing)
            {
                detectionMissCount++;

                // Cho phép trượt vài lượt không thấy điểm nào trước khi ẩn khung "đang dò tìm",
                // tương tự cơ chế maxMissFrames ở dưới cho khung đã decode được.
                if (detectionMissCount <= maxDetectionMissFrames)
                {
                    return;
                }

                isPreviewBoxShowing = false;
            }

            // Cho phép trượt vài lượt decode (mờ/rung) trước khi coi như QR đã rời khỏi khung hình.
            // Lưu ý: đơn vị giờ là "lượt decode" (mỗi decodeInterval giây), không còn là "frame video" như trước.
            if (missFrameCount > maxMissFrames)
            {
                currentQRValue = null;
                qrBox.SetVisible(false);
            }
        }
    }

    /// <summary>
    /// ZXing gọi callback này TRÊN THREAD NỀN (bên trong barcodeReader.Decode()) mỗi khi tìm thấy
    /// 1 điểm có khả năng là finder pattern của QR - kể cả khi cuối cùng KHÔNG decode được nội
    /// dung. Chỉ gom điểm vào buffer, không đụng tới bất kỳ API Unity nào ở đây (không an toàn
    /// trên thread nền) - Update() ở main thread sẽ đọc snapshot ra sau. Chỉ được đăng ký khi
    /// showBoxOnDetectionOnly = true (xem Start()).
    /// </summary>
    private void OnPossibleResultPointFound(ResultPoint point)
    {
        lock (detectionLock)
        {
            detectedPointsBuffer.Add(point);
        }
    }

    /// <summary>
    /// Vẽ khung ƯỚC LƯỢNG (hình chữ nhật thẳng bao quanh các điểm ZXing tìm thấy) khi mới chỉ
    /// PHÁT HIỆN được hình QR, chưa đọc được nội dung. Khác với UpdateBoxCorners(): ở bước này
    /// ZXing chưa trả về Result nên KHÔNG có ResultMetadataType.ORIENTATION để xoay ngược điểm về
    /// đúng hệ toạ độ gốc, và số điểm/tên gọi (bottom-left/top-left/top-right...) cũng không đảm
    /// bảo đúng thứ tự như khi decode thành công - vì vậy khung ở trạng thái này chỉ mang tính báo
    /// hiệu "có QR trong khung hình", có thể không khớp góc/hướng chính xác 100% với QR thật, và
    /// sẽ tự động chuyển sang khung chính xác (có xoay đúng góc) ngay khi đọc được nội dung QR.
    /// </summary>
    private void ShowDetectionPreviewBox(List<ResultPoint> points)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        for (int i = 0; i < points.Count; i++)
        {
            Vector2 local = PixelToLocalRaw(points[i]);
            if (local.x < minX) minX = local.x;
            if (local.x > maxX) maxX = local.x;
            if (local.y < minY) minY = local.y;
            if (local.y > maxY) maxY = local.y;
        }

        Vector2 topLeft = new Vector2(minX, maxY);
        Vector2 topRight = new Vector2(maxX, maxY);
        Vector2 bottomRight = new Vector2(maxX, minY);
        Vector2 bottomLeft = new Vector2(minX, minY);

        isPreviewBoxShowing = true;
        qrBox.SetVisible(true);
        qrBox.SetColor(colorDetecting);
        qrBox.SetLabel("Đang dò tìm QR...");
        qrBox.SetCorners(topLeft, topRight, bottomRight, bottomLeft);
    }

    /// <summary>
    /// Giống RawPixelToLocal() nhưng KHÔNG xoay ngược theo ORIENTATION, vì ở bước phát hiện sớm
    /// (ShowDetectionPreviewBox) ZXing chưa trả Result nên không có metadata để biết đã xoay
    /// bao nhiêu độ. Chỉ dùng cho khung ước lượng, không dùng cho khung chính xác.
    /// </summary>
    private Vector2 PixelToLocalRaw(ResultPoint point)
    {
        Rect rect = rawImage.rectTransform.rect;

        float u = point.X / FrameWidth;
        float v = point.Y / FrameHeight;

        if (invertVerticalMapping)
        {
            v = 1f - v;
        }

        float localX = rect.xMin + u * rect.width;
        float localY = rect.yMin + v * rect.height;

        return new Vector2(localX, localY);
    }

    /// <summary>
    /// Vẽ khung CHÍNH XÁC quanh QR thật, dựa trên toạ độ GÓC NGOÀI thực tế của QR chứ không
    /// phải 1 hệ số phóng to cố định (boxScale kiểu cũ). 3 điểm ZXing trả về (bottom-left,
    /// top-left, top-right) là TÂM của 3 finder pattern (ô vuông định vị), luôn cách mép ngoài
    /// QR đúng 3.5 module - nên chỉ cần biết kích thước 1 module (pixel) là "nới" tâm ra đúng
    /// 3.5 module theo 2 hướng cạnh để ra đúng góc ngoài, KHÔNG phụ thuộc QR có bao nhiêu module
    /// (tức không phụ thuộc độ dài nội dung mã vé) như cách nhân hệ số cũ (chỉ đúng với 1 cỡ QR
    /// cố định, sai với mọi cỡ khác). Kích thước module lấy thẳng từ
    /// FinderPattern.EstimatedModuleSize (ZXing.Net tự ước lượng lúc dò tìm finder pattern).
    /// </summary>
    private void UpdateBoxCorners(Result result)
    {
        ResultPoint[] points = result.ResultPoints;

        // Quy ước của ZXing.Net cho QR: points[0] = bottom-left, [1] = top-left, [2] = top-right.
        Vector2 bottomLeft = UnrotateResultPoint(points[0], result);
        Vector2 topLeft = UnrotateResultPoint(points[1], result);
        Vector2 topRight = UnrotateResultPoint(points[2], result);

        float outset = EstimateModuleSize(points, topLeft, topRight) * 3.5f;

        Vector2 rightDir = (topRight - topLeft).normalized;
        Vector2 downDir = (bottomLeft - topLeft).normalized;

        Vector2 topLeftOuter = topLeft - rightDir * outset - downDir * outset;
        Vector2 topRightOuter = topRight + rightDir * outset - downDir * outset;
        Vector2 bottomLeftOuter = bottomLeft - rightDir * outset + downDir * outset;

        // Ước lượng góc còn lại theo hình bình hành - đủ chính xác để vẽ khung.
        Vector2 bottomRightOuter = topRightOuter + bottomLeftOuter - topLeftOuter;

        qrBox.SetCorners(
            RawPixelToLocal(topLeftOuter),
            RawPixelToLocal(topRightOuter),
            RawPixelToLocal(bottomRightOuter),
            RawPixelToLocal(bottomLeftOuter));
    }

    /// <summary>
    /// Kích thước trung bình 1 module QR (pixel), lấy từ EstimatedModuleSize của 3 finder
    /// pattern ZXing trả về. Nhánh fallback thực tế không nên xảy ra (ZXing.Net luôn trả về
    /// FinderPattern - kế thừa ResultPoint - cho mọi QR decode thành công) nhưng vẫn giữ để
    /// không bao giờ chia cho 0 / vẽ khung NaN nếu 1 phiên bản ZXing khác thay đổi hành vi này.
    /// </summary>
    private float EstimateModuleSize(ResultPoint[] points, Vector2 topLeft, Vector2 topRight)
    {
        float sum = 0f;
        int count = 0;

        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] is FinderPattern fp && fp.EstimatedModuleSize > 0f)
            {
                sum += fp.EstimatedModuleSize;
                count++;
            }
        }

        if (count > 0)
            return sum / count;

        // Coi như QR ~25 module/cạnh (cỡ phổ biến với ticketCode ngắn ~10-20 ký tự).
        return Vector2.Distance(topLeft, topRight) / (25f - 7f);
    }

    /// <summary>
    /// Chuyển 1 toạ độ pixel (đã ở hệ toạ độ gốc cameraTexture, đã unrotate nếu cần) sang local
    /// position trong RectTransform của RawImage. Vì QRBox là con của RawImage nên phần xoay/lật
    /// ở ApplyCameraOrientation() sẽ tự động áp dụng luôn cho khung, không cần tính lại ở đây.
    /// </summary>
    private Vector2 RawPixelToLocal(Vector2 rawPixel)
    {
        Rect rect = rawImage.rectTransform.rect;

        float u = rawPixel.x / FrameWidth;
        float v = rawPixel.y / FrameHeight;

        if (invertVerticalMapping)
        {
            v = 1f - v;
        }

        float localX = rect.xMin + u * rect.width;
        float localY = rect.yMin + v * rect.height;

        return new Vector2(localX, localY);
    }

    /// <summary>
    /// Xoay ngược 1 điểm ZXing trả về (đang ở hệ toạ độ ảnh ĐÃ XOAY do AutoRotate) về lại hệ
    /// toạ độ ảnh GỐC (cameraTexture.width x height). Số độ đã xoay lấy từ
    /// result.ResultMetadata[ResultMetadataType.ORIENTATION] (ZXing tự set, luôn là bội số
    /// của 90). Công thức suy ra trực tiếp từ cách ZXing.Net triển khai rotateCounterClockwise()
    /// trong BaseLuminanceSource.cs (xnew = yold, ynew = width - 1 - xold mỗi lần xoay 90°).
    /// </summary>
    private Vector2 UnrotateResultPoint(ResultPoint point, Result result)
    {
        int orientation = 0;

        if (result.ResultMetadata != null &&
            result.ResultMetadata.TryGetValue(ResultMetadataType.ORIENTATION, out object orientationObj))
        {
            orientation = ((System.Convert.ToInt32(orientationObj) % 360) + 360) % 360;
        }

        if (orientation == 0)
            return new Vector2(point.X, point.Y);

        int w = FrameWidth;
        int h = FrameHeight;

        switch (orientation)
        {
            case 90:
                return new Vector2(w - 1 - point.Y, point.X);
            case 180:
                return new Vector2(w - 1 - point.X, h - 1 - point.Y);
            case 270:
                return new Vector2(point.Y, h - 1 - point.X);
            default:
                return new Vector2(point.X, point.Y);
        }
    }

    /// <summary>
    /// Xoay, lật RawImage để khớp hướng thật của camera, và tự tính kích thước "phủ kín"
    /// (cover) khung UI theo đúng tỉ lệ camera - có TÍNH ĐẾN việc RawImage sẽ bị xoay.
    /// KHÔNG dùng Unity AspectRatioFitter vì nó không "rotation-aware": nó tính sizeDelta
    /// dựa trên rect của parent trong không gian CHƯA xoay, nên với góc xoay 90/270 độ
    /// (rất phổ biến khi cầm điện thoại dọc) nó luôn cho ra kích thước phủ SAI - thường là
    /// zoom/crop quá mức, mất nhiều pixel hơn cần thiết. Đây là hạn chế đã biết của
    /// AspectRatioFitter (xem: https://discussions.unity.com/t/rotation-and-scaling-zooming-of-camera-feed-with-webcamtexture/919502),
    /// không phải bug riêng của project này.
    /// </summary>
    private void ApplyCameraOrientation()
    {
        if (rawImage == null)
            return;

        RectTransform parentRect = rawImage.rectTransform.parent as RectTransform;
        if (parentRect == null)
            return;

        // ApplyCameraOrientation() chỉ được gọi từ UpdateDeviceCamera() (webcam thật) - Test Mode
        // (UpdateTestMode()) không gọi hàm này vì không hiển thị gì qua rawImage nữa (xem
        // StartTestMode()), nên không cần quan tâm rotation/mirror ở đây.
        int rotationAngle = cameraTexture.videoRotationAngle;

        // Xoay RawImage quanh tâm. Yêu cầu pivot của RectTransform = (0.5, 0.5).
        rawImage.rectTransform.localEulerAngles = new Vector3(0f, 0f, -rotationAngle);

        // Lật theo trục Y nếu camera trả về ảnh gương (thường gặp ở camera trước).
        float scaleY = cameraTexture.videoVerticallyMirrored ? -1f : 1f;
        rawImage.rectTransform.localScale = new Vector3(1f, scaleY, 1f);

        // QUAN TRỌNG: tỉ lệ camera dùng để tính cover LUÔN LÀ width/height GỐC, KHÔNG hoán đổi
        // dù ảnh bị xoay 90/270 hay không. Tỉ lệ giữa 2 cạnh ảnh gốc không đổi khi xoay - chỉ có
        // CÁCH nó hiển thị trên màn hình đổi thôi (phần xoay ở trên đã tự lo việc đó rồi).
        // Hoán đổi tỉ lệ ở bước này (như code cũ) là tính sai kép.
        float cameraRatio = (float)cameraTexture.width / cameraTexture.height;

        Vector2 parentSize = parentRect.rect.size;
        bool isPortraitRotation = (rotationAngle == 90 || rotationAngle == 270);

        // Sau khi xoay 90/270 độ, cạnh "rộng" của RawImage (trong không gian local, chưa xoay)
        // sẽ hiển thị thành cạnh "cao" trên màn hình và ngược lại -> phải tính cover theo cặp
        // kích thước ĐÃ hoán đổi của parent, chứ không phải kích thước gốc của parent.
        float coverW = isPortraitRotation ? parentSize.y : parentSize.x;
        float coverH = isPortraitRotation ? parentSize.x : parentSize.y;

        float targetW, targetH;
        if (coverW / coverH < cameraRatio)
        {
            // Bị giới hạn bởi chiều cao -> chiều rộng tràn ra ngoài (đúng kiểu crop-to-cover).
            targetH = coverH;
            targetW = coverH * cameraRatio;
        }
        else
        {
            targetW = coverW;
            targetH = coverW / cameraRatio;
        }

        // Nếu RawImage dùng anchor kéo giãn (stretch) theo parent thì sizeDelta là phần "dư"
        // so với kích thước parent; nếu dùng anchor điểm cố định thì sizeDelta chính là
        // kích thước thật. Xem tooltip của rawImageUsesStretchedAnchors.
        rawImage.rectTransform.sizeDelta = rawImageUsesStretchedAnchors
            ? new Vector2(targetW - parentSize.x, targetH - parentSize.y)
            : new Vector2(targetW, targetH);
    }

    private void OnQRCodeDetected(string value)
    {
        // Bắn event cho các script khác (vd TicketManager) xử lý mã vé.
        // Xem TicketManager.ProcessTicketCode() + FirebaseManager.CheckInTicket().
        OnQRCodeScanned?.Invoke(value);
    }

    /// <summary>
    /// Gọi từ bên ngoài (vd FirebaseManager) sau khi có kết quả kiểm tra vé,
    /// để cập nhật màu khung + text hiển thị quanh QR.
    /// </summary>
    public void UpdateScanStatus(string qrValue, ScanState state, string message)
    {
        // Nếu QR này đã rời khỏi khung hình hoặc đã bị thay bằng QR khác
        // thì bỏ qua kết quả trễ (tránh hiện sai trạng thái).
        if (qrValue != currentQRValue)
            return;

        currentState = state;

        Color color;
        switch (state)
        {
            case ScanState.Valid:
                color = colorValid;
                break;
            case ScanState.Invalid:
                color = colorInvalid;
                break;
            default:
                color = colorProcessing;
                break;
        }

        qrBox.SetColor(color);
        qrBox.SetLabel(message);
    }

    /// <summary>
    /// Tạo 1 Text hiển thị trạng thái camera (đang khởi động / lỗi quyền / không tìm thấy
    /// camera), làm CON CỦA PARENT của RawImage (không phải con của RawImage) để KHÔNG bị xoay
    /// theo ApplyCameraOrientation() - khác với QRBoxUI (cố ý là con của RawImage để tự ăn theo
    /// xoay/lật, xem QRBoxUI class doc), thông báo này cần luôn hiển thị thẳng đứng dễ đọc bất kể
    /// máy đang xoay theo hướng nào. Vì được tạo sau RawImage nên tự nhiên nằm trên cùng
    /// (sibling cuối cùng) trong cùng Canvas, không cần chỉnh sorting order riêng.
    /// </summary>
    private Text BuildCameraStatusLabel()
    {
        RectTransform canvasRect = rawImage.rectTransform.parent as RectTransform;
        if (canvasRect == null)
            return null;

        GameObject go = new GameObject("CameraStatusText", typeof(RectTransform));
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(canvasRect, false);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(760f, 220f);

        Text text = go.AddComponent<Text>();

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
        {
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        text.font = font;
        text.alignment = TextAnchor.MiddleCenter;
        text.fontSize = 30;
        text.fontStyle = FontStyle.Bold;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);

        go.SetActive(false);

        return text;
    }

    /// <summary>
    /// message = null/rỗng -> ẩn label. Ngược lại hiện label với nội dung đó.
    /// Dùng cho mọi trạng thái camera (khởi động/lỗi quyền/không có camera) - xem
    /// RequestCameraPermissionThenStart() và StartCamera().
    /// </summary>
    private void SetCameraStatus(string message)
    {
        if (cameraStatusText == null)
            return;

        bool show = !string.IsNullOrEmpty(message);
        cameraStatusText.gameObject.SetActive(show);

        if (show)
        {
            cameraStatusText.text = message;
        }
    }

    private void OnDestroy()
    {
        if (cameraTexture != null)
        {
            cameraTexture.Stop();
        }

        if (activeTestCamera != null)
        {
            // Trả lại targetDisplay/targetTexture gốc cho Camera - Test Mode đã đổi 2 giá trị này
            // trong StartTestMode() để camera render thẳng ra Display 1.
            activeTestCamera.targetDisplay = testCameraOriginalTargetDisplay;
            activeTestCamera.targetTexture = testCameraOriginalTargetTexture;
        }
    }
}