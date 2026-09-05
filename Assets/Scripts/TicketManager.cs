using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Nhận mã QR từ CameraScanner (qua event OnQRCodeScanned), kiểm tra định dạng,
/// rồi nối qua FirebaseManager để so khớp/checkin vé trên Firestore.
/// Kết quả được đẩy ngược lại CameraScanner qua UpdateScanStatus() để đổi màu khung.
/// </summary>
public class TicketManager : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CameraScanner cameraScanner;
    [SerializeField] private FirebaseManager firebaseManager;

    [Header("Validation")]
    [Tooltip("Regex kiểm tra định dạng mã vé TRƯỚC khi query Firestore, chỉ để lọc rác " +
             "(QR chứa URL, JSON, text linh tinh...) chứ KHÔNG giả định prefix cố định vì " +
             "ticketCode được sinh random. Mặc định: chỉ chữ/số/dash/underscore, dài 4-64 ký tự.")]
    private string ticketCodePattern = @"^[A-Za-z0-9_-]{4,64}$";

    [Header("Test / Debug")]
    [Tooltip("Nhập mã vé (giá trị field ticketCode, KHÔNG phải document ID) vào đây rồi " +
             "bấm chuột phải vào component -> \"Reset Ticket (Debug Ticket Code)\" để test lại nhiều lần.")]
    [SerializeField] private string debugTicketCode;

    private void OnEnable()
    {
        if (cameraScanner != null)
        {
            cameraScanner.OnQRCodeScanned += HandleQRCodeScanned;
        }
    }

    private void OnDisable()
    {
        if (cameraScanner != null)
        {
            cameraScanner.OnQRCodeScanned -= HandleQRCodeScanned;
        }
    }

    private void HandleQRCodeScanned(string qrValue)
    {
        ProcessTicketCode(qrValue);
    }

    /// <summary>
    /// Xử lý 1 mã vừa quét được: validate format -> query/checkin Firestore -> báo lại CameraScanner.
    /// Public để có thể gọi tay (test) hoặc từ nguồn khác ngoài CameraScanner nếu cần.
    /// </summary>
    public void ProcessTicketCode(string qrValue)
    {
        if (!IsValidFormat(qrValue))
        {
            Debug.LogWarning("TicketManager: Mã QR sai định dạng -> " + qrValue);
            cameraScanner?.UpdateScanStatus(qrValue, ScanState.Invalid, "Mã QR không đúng định dạng");
            return;
        }

        if (firebaseManager == null)
        {
            Debug.LogError("TicketManager: Chưa gán FirebaseManager trong Inspector!");
            cameraScanner?.UpdateScanStatus(qrValue, ScanState.Invalid, "Lỗi hệ thống");
            return;
        }

        // Mã này đã được CHÍNH MÁY NÀY xác nhận check-in trước đó (xem LocalCheckInCache class
        // doc) - kết quả tra Firestore chắc chắn vẫn là "đã check-in", nên báo luôn tại chỗ thay
        // vì tốn 1 lượt đọc Firestore cho 1 câu trả lời đã biết trước.
        if (LocalCheckInCache.Contains(qrValue))
        {
            cameraScanner?.UpdateScanStatus(qrValue, ScanState.Invalid, "Vé đã được check-in trước đó");
            return;
        }

        firebaseManager.CheckInTicket(qrValue, result =>
        {
            ScanState state = result.Success ? ScanState.Valid : ScanState.Invalid;

            // Cache cả 2 trường hợp: check-in THÀNH CÔNG (lần đầu) và PHÁT HIỆN đã check-in từ
            // trước (lần đầu tiên máy này biết tin này, có thể do máy khác check-in) - cả 2 đều
            // là kết quả CHẮC CHẮN không đổi cho lần quét sau. Không cache lỗi tạm thời (mất
            // mạng, vé không tồn tại) vì đó có thể tự hết khi thử lại.
            if (result.Success || result.AlreadyCheckedIn)
            {
                LocalCheckInCache.Add(qrValue);
            }

            cameraScanner?.UpdateScanStatus(qrValue, state, result.Message);
        });
    }

    private bool IsValidFormat(string code)
    {
        if (string.IsNullOrEmpty(code))
            return false;

        if (string.IsNullOrEmpty(ticketCodePattern))
            return true; // Không có pattern -> bỏ qua bước validate format

        return Regex.IsMatch(code, ticketCodePattern);
    }

    /// <summary>
    /// Set checkedIn = false cho mã vé nhập ở "Debug Ticket Code", để quét test lại nhiều lần.
    /// Bấm chuột phải vào tiêu đề component TicketManager trong Inspector (kể cả lúc đang Play) để gọi.
    /// </summary>
    [ContextMenu("Reset Ticket (Debug Ticket Code)")]
    private void ResetDebugTicket()
    {
        if (string.IsNullOrEmpty(debugTicketCode))
        {
            Debug.LogWarning("TicketManager: Chưa nhập mã vé vào field 'Debug Ticket Code'!");
            return;
        }

        if (firebaseManager == null)
        {
            Debug.LogError("TicketManager: Chưa gán FirebaseManager trong Inspector!");
            return;
        }

        firebaseManager.ResetTicketCheckIn(debugTicketCode, (success, message) =>
        {
            if (success)
            {
                Debug.Log("[TicketManager] " + message);
            }
            else
            {
                Debug.LogError("[TicketManager] " + message);
            }
        });
    }
}