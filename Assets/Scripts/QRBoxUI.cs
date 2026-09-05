using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Vẽ khung quanh QR code theo kiểu "4 góc" (giống viewfinder các app quét mã phổ biến): thay vì tự
/// dựng hình bằng code, phần hình ảnh giờ lấy từ 1 Prefab do người thiết kế UI chuẩn bị sẵn
/// (xem cornerBoxPrefab trên CameraScanner - vd Assets/Prefabs/BoxQR.prefab), gồm 1 RectTransform
/// gốc + 4 Image con (mỗi con 1 góc, đã có sẵn Sprite + hướng đúng). Class này CHỈ lo vị trí/kích
/// thước/xoay của CẢ KHỐI prefab đó (dịch chuyển + xoay + scale nguyên khối theo khung QR thật tính
/// được mỗi frame) và đổi màu tất cả Image con cùng lúc theo trạng thái quét - không quan tâm prefab
/// có bao nhiêu con hay đặt tên gì.
///
/// Yêu cầu ở prefab để phần scale hoạt động đúng: RectTransform gốc của prefab phải có
/// anchorMin = anchorMax = pivot = (0.5, 0.5), và kích thước (Width/Height trong Inspector -
/// Rect Transform) của nó được dùng làm "kích thước thiết kế" tham chiếu - class này đọc kích
/// thước đó 1 lần lúc Instantiate rồi tự tính tỉ lệ scale cần áp cho vừa đúng khung QR thật, nên
/// người thiết kế có thể dựng prefab ở bất kỳ kích thước nào thấy tiện, không cần khớp số cụ thể
/// nào trong code.
///
/// Toàn bộ (root QRBox này) được tạo ra lúc runtime và làm con của RawImage, nên tự động ăn theo
/// rotation/scale (lật gương) mà CameraScanner đã set cho RawImage — không cần tính toán bù trừ
/// xoay/lật ở đây.
/// </summary>
public class QRBoxUI : MonoBehaviour
{
    [Header("Box Adjustment")]
    [Tooltip("Hệ số phóng to khung (quanh tâm khung) SAU KHI CameraScanner.UpdateBoxCorners() đã " +
             "tính đúng 4 góc ngoài thật của QR (dựa trên FinderPattern.EstimatedModuleSize của " +
             "ZXing) - dùng làm khoảng đệm thẩm mỹ nhỏ (viền không đè sát lên mép QR). 1 = ôm khít " +
             "đúng mép QR, >1 = có đệm.")]
    [SerializeField] private float boxScale = 1.15f;

    [Tooltip("Góc xoay bù (độ) áp dụng cho khung quanh tâm của chính nó. LƯU Ý: kể từ khi " +
             "CameraScanner.UnrotateResultPoint() được thêm vào để xoay ngược điểm ZXing về đúng " +
             "hệ toạ độ gốc của cameraTexture, khung đã tự động xoay đúng theo camera nhờ QRBox là " +
             "con của RawImage (xem class doc phía trên) - KHÔNG cần bù thêm ở đây nữa. Giá trị này " +
             "chỉ nên khác 0 nếu sau khi test trên máy khung vẫn lệch một góc CỐ ĐỊNH (không phải do " +
             "sai hệ toạ độ) mà UnrotateResultPoint() chưa xử lý hết.")]
    [SerializeField] private float rotationCorrectionDegrees = 0f;

    // RectTransform gốc của prefab đã Instantiate (vd BoxQR.prefab) - SetCorners() chỉ dịch
    // chuyển/xoay/scale NGUYÊN KHỐI transform này, không đụng tới từng con bên trong.
    private RectTransform cornerBoxRoot;
    private float designWidth = 1f;
    private float designHeight = 1f;

    // Tất cả Image con bên trong prefab (bất kể tên/số lượng) - dùng để đổi màu đồng loạt
    // theo trạng thái quét (SetColor).
    private Image[] cornerImages;

    private Text statusText;

    public static QRBoxUI Create(RectTransform parent, GameObject cornerBoxPrefab)
    {
        GameObject root = new GameObject("QRBox", typeof(RectTransform));
        RectTransform rootRT = root.GetComponent<RectTransform>();
        rootRT.SetParent(parent, false);
        rootRT.anchorMin = Vector2.zero;
        rootRT.anchorMax = Vector2.one;
        rootRT.offsetMin = Vector2.zero;
        rootRT.offsetMax = Vector2.zero;
        rootRT.pivot = new Vector2(0.5f, 0.5f);

        QRBoxUI box = root.AddComponent<QRBoxUI>();
        box.BuildCornerBox(cornerBoxPrefab);
        box.BuildLabel();
        box.SetVisible(false);

        return box;
    }

    /// <summary>
    /// Instantiate cornerBoxPrefab (vd BoxQR.prefab) làm con của QRBox. Đọc sizeDelta gốc của
    /// prefab làm "kích thước thiết kế" tham chiếu để tính tỉ lệ scale mỗi lần SetCorners() -
    /// xem class doc phía trên.
    /// </summary>
    private void BuildCornerBox(GameObject cornerBoxPrefab)
    {
        if (cornerBoxPrefab == null)
        {
            Debug.LogError("QRBoxUI: Chưa gán Corner Box Prefab (CameraScanner.cornerBoxPrefab) - " +
                            "khung QR sẽ không hiển thị.");
            return;
        }

        GameObject instance = Instantiate(cornerBoxPrefab, transform, false);
        cornerBoxRoot = instance.GetComponent<RectTransform>();

        if (cornerBoxRoot == null)
        {
            Debug.LogError("QRBoxUI: cornerBoxPrefab phải có RectTransform ở GameObject gốc " +
                            "(anchorMin = anchorMax = pivot = (0.5, 0.5)) - hiện đang thiếu.");
            return;
        }

        designWidth = Mathf.Max(1f, cornerBoxRoot.rect.width);
        designHeight = Mathf.Max(1f, cornerBoxRoot.rect.height);

        cornerImages = instance.GetComponentsInChildren<Image>(true);
    }

    private void BuildLabel()
    {
        GameObject go = new GameObject("StatusText", typeof(RectTransform));
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(transform, false);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(260f, 44f);

        statusText = go.AddComponent<Text>();

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
        {
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        statusText.font = font;
        statusText.alignment = TextAnchor.MiddleCenter;
        statusText.fontSize = 26;
        statusText.fontStyle = FontStyle.Bold;
        statusText.color = Color.white;
        statusText.raycastTarget = false;

        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);
    }

    public void SetVisible(bool visible)
    {
        gameObject.SetActive(visible);
    }

    public void SetColor(Color color)
    {
        if (cornerImages != null)
        {
            for (int i = 0; i < cornerImages.Length; i++)
            {
                cornerImages[i].color = color;
            }
        }

        statusText.color = color;
    }

    public void SetLabel(string message)
    {
        statusText.text = message;
        statusText.gameObject.SetActive(!string.IsNullOrEmpty(message));
    }

    /// <summary>
    /// corners tính bằng local position (đơn vị pixel UI) trong không gian local của RawImage —
    /// xem CameraScanner.UpdateBoxCorners()/RawPixelToLocal(). Sau khi phóng to (boxScale)/xoay bù
    /// (rotationCorrectionDegrees) quanh tâm khung, suy ra tâm + kích thước + góc xoay của CẢ khối
    /// khung QR thật, rồi áp nguyên khối vào cornerBoxRoot (dịch chuyển/xoay/scale) - 4 góc bên
    /// trong prefab tự động đi theo đúng vị trí tương đối đã thiết kế sẵn, không cần tính riêng
    /// từng góc ở đây.
    /// </summary>
    public void SetCorners(Vector2 topLeft, Vector2 topRight, Vector2 bottomRight, Vector2 bottomLeft)
    {
        Vector2 centroid = (topLeft + topRight + bottomRight + bottomLeft) * 0.25f;

        topLeft = TransformCorner(topLeft, centroid);
        topRight = TransformCorner(topRight, centroid);
        bottomRight = TransformCorner(bottomRight, centroid);
        bottomLeft = TransformCorner(bottomLeft, centroid);

        if (cornerBoxRoot != null)
        {
            Vector2 topEdge = topRight - topLeft;
            Vector2 leftEdge = bottomLeft - topLeft;

            float width = topEdge.magnitude;
            float height = leftEdge.magnitude;
            float angle = Mathf.Atan2(topEdge.y, topEdge.x) * Mathf.Rad2Deg;

            cornerBoxRoot.anchoredPosition = centroid;
            cornerBoxRoot.localEulerAngles = new Vector3(0f, 0f, angle);
            cornerBoxRoot.localScale = new Vector3(width / designWidth, height / designHeight, 1f);
        }

        Vector2 topCenter = (topLeft + topRight) * 0.5f;
        Vector2 labelTopEdge = topRight - topLeft;
        Vector2 outwardNormal = new Vector2(-labelTopEdge.y, labelTopEdge.x).normalized;
        Vector2 bottomCenter = (bottomLeft + bottomRight) * 0.5f;
        if (Vector2.Dot(outwardNormal, bottomCenter - topCenter) > 0f)
        {
            outwardNormal = -outwardNormal;
        }

        float topEdgeLength = labelTopEdge.magnitude;

        RectTransform labelRT = statusText.rectTransform;
        labelRT.anchoredPosition = topCenter + outwardNormal * (labelRT.rect.height * 0.5f + 16f);
        labelRT.sizeDelta = new Vector2(Mathf.Max(160f, topEdgeLength), 44f);
        float labelAngle = Mathf.Atan2(labelTopEdge.y, labelTopEdge.x) * Mathf.Rad2Deg;
        labelRT.localEulerAngles = new Vector3(0f, 0f, labelAngle);
    }

    /// <summary>
    /// Phóng to (boxScale) và xoay (rotationCorrectionDegrees) 1 điểm góc quanh tâm khung.
    /// </summary>
    private Vector2 TransformCorner(Vector2 corner, Vector2 centroid)
    {
        Vector2 offset = (corner - centroid) * boxScale;

        if (rotationCorrectionDegrees != 0f)
        {
            float rad = rotationCorrectionDegrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);
            offset = new Vector2(
                offset.x * cos - offset.y * sin,
                offset.x * sin + offset.y * cos
            );
        }

        return centroid + offset;
    }
}
