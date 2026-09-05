using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using ZXing;
using ZXing.Common;

/// <summary>
/// Logic cho panel admin (nhân viên cổng huỷ vé theo ghế / tạo vé thủ công
/// kèm QR sinh tại chỗ). KHÔNG tự dựng UI — toàn bộ GameObject/Button/
/// InputField được tạo sẵn trong Scene bởi Editor tool
/// (Assets/Editor/AdminPanelUIBuilder.cs, menu "LumiShow/Build Admin Panel
/// UI") và gán vào các field bên dưới qua Inspector. Class này chỉ:
///   - Ẩn/hiện đúng panel theo trạng thái.
///   - Gọi API backend /api/admin/* (PIN kiểm tra ở server, không hardcode
///     ở client — xem admin.service.js bên repo web).
///   - Sinh QR tại chỗ bằng zxing.unity.dll đã có sẵn (cùng thư viện dùng
///     để decode ở CameraScanner.cs).
/// Mỗi public method ở đây ứng với đúng 1 Button.onClick được Editor tool
/// gắn sẵn — đừng đổi tên public method mà không cập nhật lại tool.
/// </summary>
public class AdminPanelController : MonoBehaviour
{
    [Header("Backend")]
    [SerializeField] private string apiBaseUrl = "https://lumishow-website.onrender.com/api";

    [Header("Show")]
    [SerializeField] private string defaultShowId = "son-than-thuy-quai";

    [Header("Panels (gán bởi Editor tool)")]
    [SerializeField] private GameObject pinPanel;
    [SerializeField] private GameObject menuPanel;
    [SerializeField] private GameObject cancelPanel;
    [SerializeField] private GameObject createPanel;

    [Header("PIN Panel")]
    [SerializeField] private InputField pinInput;
    [SerializeField] private Text pinStatusText;

    [Header("Menu Panel")]
    [SerializeField] private Text menuStatusText;

    [Header("Cancel Panel")]
    [SerializeField] private Text cancelShowtimeLabel;
    [SerializeField] private InputField cancelSeatInput;
    [SerializeField] private Text cancelStatusText;

    [Header("Create Panel")]
    [SerializeField] private Text createShowtimeLabel;
    [SerializeField] private InputField createSeatInput;
    [SerializeField] private InputField createNameInput;
    [SerializeField] private InputField createPhoneInput;
    [SerializeField] private InputField createEmailInput;
    [SerializeField] private Text createStatusText;
    [SerializeField] private GameObject createQrContainer;
    [SerializeField] private RawImage createQrImage;
    [SerializeField] private GameObject createCodeContainer;
    [SerializeField] private Text createCodeText;

    // Đăng nhập 1 lần / phiên chạy app — không hỏi lại PIN mỗi lần thao tác,
    // chỉ hỏi lại sau khi bấm "Đăng xuất" hoặc mở lại app.
    private string sessionPassword = null;

    private void Awake()
    {
        HideAllPanels();
    }

    private void HideAllPanels()
    {
        if (pinPanel) pinPanel.SetActive(false);
        if (menuPanel) menuPanel.SetActive(false);
        if (cancelPanel) cancelPanel.SetActive(false);
        if (createPanel) createPanel.SetActive(false);
    }

    private bool AnyPanelOpen()
    {
        return (pinPanel && pinPanel.activeSelf)
            || (menuPanel && menuPanel.activeSelf)
            || (cancelPanel && cancelPanel.activeSelf)
            || (createPanel && createPanel.activeSelf);
    }

    // =========================================================
    // ĐIỀU HƯỚNG — gắn vào Button.onClick tương ứng
    // =========================================================

    /// <summary>Nút "Admin" góc màn hình.</summary>
    public void OnAdminButtonClicked()
    {
        if (AnyPanelOpen()) return; // đã mở panel nào rồi thì bỏ qua, tránh mở chồng

        if (sessionPassword != null) ShowMenuPanel();
        else ShowPinPanel();
    }

    private void ShowPinPanel()
    {
        HideAllPanels();
        pinPanel.SetActive(true);
        if (pinStatusText) pinStatusText.text = "";
        if (pinInput) pinInput.text = "";
    }

    private void ShowMenuPanel()
    {
        HideAllPanels();
        menuPanel.SetActive(true);
        if (menuStatusText) menuStatusText.text = "";
    }

    public void OnOpenCancelPanel()
    {
        HideAllPanels();
        cancelPanel.SetActive(true);
        if (cancelSeatInput) cancelSeatInput.text = "";
        if (cancelStatusText) cancelStatusText.text = "";

        cancelShowtimes = null;
        cancelShowtimeIndex = 0;
        if (cancelShowtimeLabel) cancelShowtimeLabel.text = "Đang tải suất diễn...";

        StartCoroutine(FetchShowtimes(list =>
        {
            cancelShowtimes = list;
            cancelShowtimeIndex = 0;
            RefreshShowtimeLabel(cancelShowtimeLabel, cancelShowtimes, cancelShowtimeIndex);
        }));
    }

    public void OnOpenCreatePanel()
    {
        HideAllPanels();
        createPanel.SetActive(true);
        if (createSeatInput) createSeatInput.text = "";
        if (createNameInput) createNameInput.text = "";
        if (createPhoneInput) createPhoneInput.text = "";
        if (createEmailInput) createEmailInput.text = "";
        if (createStatusText) createStatusText.text = "";
        if (createQrContainer) createQrContainer.SetActive(false);
        if (createCodeContainer) createCodeContainer.SetActive(false);

        createShowtimes = null;
        createShowtimeIndex = 0;
        if (createShowtimeLabel) createShowtimeLabel.text = "Đang tải suất diễn...";

        StartCoroutine(FetchShowtimes(list =>
        {
            createShowtimes = list;
            createShowtimeIndex = 0;
            RefreshShowtimeLabel(createShowtimeLabel, createShowtimes, createShowtimeIndex);
        }));
    }

    // =========================================================
    // CHỌN SUẤT DIỄN — lấy danh sách suất sắp tới thật từ backend (mùa diễn
    // kéo dài nhiều tháng, mỗi ngày trong tuần giờ diễn khác nhau, xem
    // seedSeats.js bên repo web) thay vì nhân viên phải gõ tay showtimeId.
    // ◀ ▶ chỉ lướt trong danh sách đã tải, không gọi lại mạng mỗi lần bấm.
    // =========================================================

    private List<ShowtimeOption> cancelShowtimes;
    private int cancelShowtimeIndex;
    private List<ShowtimeOption> createShowtimes;
    private int createShowtimeIndex;

    public void OnCancelShowtimePrev()
    {
        if (cancelShowtimes == null || cancelShowtimes.Count == 0) return;
        cancelShowtimeIndex = (cancelShowtimeIndex - 1 + cancelShowtimes.Count) % cancelShowtimes.Count;
        RefreshShowtimeLabel(cancelShowtimeLabel, cancelShowtimes, cancelShowtimeIndex);
    }

    public void OnCancelShowtimeNext()
    {
        if (cancelShowtimes == null || cancelShowtimes.Count == 0) return;
        cancelShowtimeIndex = (cancelShowtimeIndex + 1) % cancelShowtimes.Count;
        RefreshShowtimeLabel(cancelShowtimeLabel, cancelShowtimes, cancelShowtimeIndex);
    }

    public void OnCreateShowtimePrev()
    {
        if (createShowtimes == null || createShowtimes.Count == 0) return;
        createShowtimeIndex = (createShowtimeIndex - 1 + createShowtimes.Count) % createShowtimes.Count;
        RefreshShowtimeLabel(createShowtimeLabel, createShowtimes, createShowtimeIndex);
    }

    public void OnCreateShowtimeNext()
    {
        if (createShowtimes == null || createShowtimes.Count == 0) return;
        createShowtimeIndex = (createShowtimeIndex + 1) % createShowtimes.Count;
        RefreshShowtimeLabel(createShowtimeLabel, createShowtimes, createShowtimeIndex);
    }

    private void RefreshShowtimeLabel(Text label, List<ShowtimeOption> list, int index)
    {
        if (label == null) return;

        if (list == null)
        {
            label.text = "Đang tải suất diễn...";
        }
        else if (list.Count == 0)
        {
            label.text = "Không có suất diễn nào sắp tới";
        }
        else
        {
            // Không dùng ký tự mũi tên ◀▶ — font mặc định (LegacyRuntime/Arial)
            // không chắc có glyph đó trên mọi thiết bị. Bấm nút "<"/">" cạnh
            // bên để chuyển, label chỉ hiện tên suất diễn.
            label.text = list[index].label;
        }
    }

    private IEnumerator FetchShowtimes(Action<List<ShowtimeOption>> onDone)
    {
        string json = "{\"password\":" + JsonEscape(sessionPassword) + "}";

        using (UnityWebRequest req = BuildPostRequest("/admin/showtimes/list", json))
        {
            yield return req.SendWebRequest();

            string body = req.downloadHandler != null ? req.downloadHandler.text : "";
            ShowtimesResponse res = null;
            try { res = JsonUtility.FromJson<ShowtimesResponse>(body); } catch { }

            if (req.result == UnityWebRequest.Result.Success && res != null && res.success && res.showtimes != null)
            {
                onDone(new List<ShowtimeOption>(res.showtimes));
            }
            else
            {
                onDone(new List<ShowtimeOption>());
            }
        }
    }

    /// <summary>
    /// Xoá LocalCheckInCache (file JSON cục bộ trên máy này - xem LocalCheckInCache class doc),
    /// KHÔNG đụng gì tới dữ liệu checkedIn trên Firestore. Vì đây là thao tác cục bộ, không phải
    /// dữ liệu (không thể huỷ/tạo nhầm vé), không cần gọi qua backend/PIN-check server-side như
    /// huỷ/tạo vé - chỉ cần đã vào được menu admin (PIN đã xác thực để mở menu) là đủ.
    /// </summary>
    public void OnClearLocalCache()
    {
        int clearedCount = LocalCheckInCache.Clear();

        if (menuStatusText)
        {
            menuStatusText.color = new Color(0.6f, 1f, 0.6f);
            menuStatusText.text = "Đã xoá " + clearedCount + " mã khỏi cache check-in cục bộ.";
        }
    }

    public void OnBackToMenu()
    {
        ShowMenuPanel();
    }

    public void OnLogout()
    {
        sessionPassword = null;
        HideAllPanels();
    }

    public void OnCloseAll()
    {
        HideAllPanels();
    }

    // =========================================================
    // XÁC THỰC PIN
    // =========================================================

    public void OnVerifyPinClicked()
    {
        string pin = pinInput != null ? pinInput.text : "";
        if (pinStatusText) pinStatusText.text = "Đang kiểm tra...";

        StartCoroutine(VerifyPin(pin, ok =>
        {
            if (ok)
            {
                sessionPassword = pin;
                ShowMenuPanel();
            }
            else if (pinStatusText)
            {
                pinStatusText.text = "Sai mật khẩu, thử lại.";
            }
        }));
    }

    private IEnumerator VerifyPin(string pin, Action<bool> onDone)
    {
        string json = "{\"password\":" + JsonEscape(pin) + "}";

        using (UnityWebRequest req = BuildPostRequest("/admin/verify-pin", json))
        {
            yield return req.SendWebRequest();
            onDone(req.result == UnityWebRequest.Result.Success);
        }
    }

    // =========================================================
    // HUỶ VÉ THEO GHẾ
    // =========================================================

    public void OnCancelSubmit()
    {
        string seatId = (cancelSeatInput != null ? cancelSeatInput.text : "").Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(seatId))
        {
            if (cancelStatusText) cancelStatusText.text = "Nhập mã ghế trước đã.";
            return;
        }

        if (cancelShowtimes == null || cancelShowtimes.Count == 0)
        {
            if (cancelStatusText) cancelStatusText.text = "Chưa chọn được suất diễn, thử mở lại panel.";
            return;
        }

        string showtimeId = cancelShowtimes[cancelShowtimeIndex].showtimeId;

        if (cancelStatusText)
        {
            cancelStatusText.color = Color.white;
            cancelStatusText.text = "Đang huỷ...";
        }

        string json = "{\"password\":" + JsonEscape(sessionPassword) +
                       ",\"showId\":" + JsonEscape(defaultShowId) +
                       ",\"showtimeId\":" + JsonEscape(showtimeId) +
                       ",\"seatId\":" + JsonEscape(seatId) + "}";

        StartCoroutine(PostAndReport("/admin/tickets/cancel", json, cancelStatusText));
    }

    // =========================================================
    // TẠO VÉ THỦ CÔNG
    // =========================================================

    public void OnCreateSubmit()
    {
        string seatId = (createSeatInput != null ? createSeatInput.text : "").Trim().ToUpperInvariant();
        string customerName = (createNameInput != null ? createNameInput.text : "").Trim();

        if (string.IsNullOrEmpty(seatId) || string.IsNullOrEmpty(customerName))
        {
            if (createStatusText) createStatusText.text = "Cần nhập mã ghế và tên khách.";
            return;
        }

        if (createShowtimes == null || createShowtimes.Count == 0)
        {
            if (createStatusText) createStatusText.text = "Chưa chọn được suất diễn, thử mở lại panel.";
            return;
        }

        string showtimeId = createShowtimes[createShowtimeIndex].showtimeId;

        if (createStatusText)
        {
            createStatusText.color = Color.white;
            createStatusText.text = "Đang tạo vé...";
        }

        if (createQrContainer) createQrContainer.SetActive(false);
        if (createCodeContainer) createCodeContainer.SetActive(false);

        string phone = createPhoneInput != null ? createPhoneInput.text.Trim() : "";
        string email = createEmailInput != null ? createEmailInput.text.Trim() : "";

        string json = "{\"password\":" + JsonEscape(sessionPassword) +
                       ",\"showId\":" + JsonEscape(defaultShowId) +
                       ",\"showtimeId\":" + JsonEscape(showtimeId) +
                       ",\"seatId\":" + JsonEscape(seatId) +
                       ",\"customerName\":" + JsonEscape(customerName) +
                       ",\"customerPhone\":" + JsonEscape(phone) +
                       ",\"customerEmail\":" + JsonEscape(email) + "}";

        StartCoroutine(CreateTicketRequest(json));
    }

    private IEnumerator CreateTicketRequest(string json)
    {
        using (UnityWebRequest req = BuildPostRequest("/admin/tickets/create", json))
        {
            yield return req.SendWebRequest();

            string body = req.downloadHandler != null ? req.downloadHandler.text : "";
            ApiResponse res = SafeParse(body);

            if (req.result != UnityWebRequest.Result.Success || res == null || !res.success)
            {
                if (createStatusText)
                {
                    createStatusText.color = new Color(1f, 0.6f, 0.6f);
                    createStatusText.text = (res != null && !string.IsNullOrEmpty(res.message)) ? res.message : "Lỗi kết nối, thử lại.";
                }
                yield break;
            }

            if (createStatusText)
            {
                createStatusText.color = new Color(0.6f, 1f, 0.6f);
                createStatusText.text = "Đã tạo vé cho ghế " + res.ticket.seatId + " (" + res.ticket.tierName + ")";
            }

            if (createQrImage != null)
            {
                Texture2D tex = GenerateQrTexture(res.ticket.ticketCode, 240);
                createQrImage.texture = tex;
            }
            if (createQrContainer) createQrContainer.SetActive(true);

            if (createCodeText) createCodeText.text = res.ticket.ticketCode;
            if (createCodeContainer) createCodeContainer.SetActive(true);
        }
    }

    // =========================================================
    // MẠNG + JSON — dùng UnityWebRequest/JsonUtility có sẵn trong Unity,
    // không thêm thư viện ngoài.
    // =========================================================

    private UnityWebRequest BuildPostRequest(string path, string jsonBody)
    {
        UnityWebRequest req = new UnityWebRequest(apiBaseUrl + path, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        return req;
    }

    private IEnumerator PostAndReport(string path, string json, Text status)
    {
        using (UnityWebRequest req = BuildPostRequest(path, json))
        {
            yield return req.SendWebRequest();

            string body = req.downloadHandler != null ? req.downloadHandler.text : "";
            ApiResponse res = SafeParse(body);

            bool ok = req.result == UnityWebRequest.Result.Success && res != null && res.success;

            if (status == null) yield break;

            if (ok)
            {
                status.color = new Color(0.6f, 1f, 0.6f);
                status.text = res.message;
            }
            else
            {
                status.color = new Color(1f, 0.6f, 0.6f);
                status.text = (res != null && !string.IsNullOrEmpty(res.message)) ? res.message : "Lỗi kết nối, thử lại.";
            }
        }
    }

    private ApiResponse SafeParse(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonUtility.FromJson<ApiResponse>(json); }
        catch { return null; }
    }

    // Escape tối thiểu cho JSON — đủ dùng cho text nhập tay (tên/SĐT/email/PIN),
    // không cần thư viện JSON đầy đủ chỉ để tạo mấy field phẳng thế này.
    private static string JsonEscape(string s)
    {
        if (s == null) s = "";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    [Serializable]
    private class ApiResponse
    {
        public bool success;
        public string message;
        public TicketResult ticket;
    }

    [Serializable]
    private class TicketResult
    {
        public string ticketCode;
        public string seatId;
        public string customerName;
        public string tierName;
        public int price;
    }

    [Serializable]
    private class ShowtimeOption
    {
        public string showtimeId;
        public string label;
    }

    [Serializable]
    private class ShowtimesResponse
    {
        public bool success;
        public string message;
        public ShowtimeOption[] showtimes;
    }

    // =========================================================
    // SINH QR TẠI CHỖ — dùng đúng zxing.unity.dll đã có sẵn trong project
    // (cùng thư viện đang dùng để DECODE ở CameraScanner.cs), không cần
    // thêm plugin nào khác.
    // =========================================================

    private Texture2D GenerateQrTexture(string content, int size)
    {
        var writer = new BarcodeWriter
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = size,
                Height = size,
                Margin = 1
            }
        };

        Color32[] pixels = writer.Write(content);

        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }
}
