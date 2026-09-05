using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Editor tool dựng sẵn toàn bộ UI thật cho panel admin (Canvas + mọi Panel/
/// Button/InputField) làm GameObject thường trong Scene — thay vì
/// AdminPanelController tự tạo bằng code lúc Play. Chạy qua menu
/// "LumiShow/Build Admin Panel UI".
///
/// Sau khi chạy: mọi thứ nằm dưới GameObject "AdminCanvas", có thể mở rộng/
/// chỉnh vị trí/màu/font trực tiếp trong Editor như UI bình thường. Chạy
/// lại được nhiều lần — nếu đã có "AdminCanvas" sẽ hỏi trước khi xoá & dựng
/// lại. Nhớ Ctrl+S để lưu Scene sau khi chạy.
/// </summary>
public static class AdminPanelUIBuilder
{
    private static Font uiFont;

    private static readonly Color ColorPrimary = new Color(0.30f, 0.65f, 0.35f);
    private static readonly Color ColorDanger = new Color(0.65f, 0.25f, 0.25f);
    private static readonly Color ColorNeutral = new Color(1f, 1f, 1f, 0.12f);

    [MenuItem("LumiShow/Build Admin Panel UI")]
    public static void Build()
    {
        GameObject existing = GameObject.Find("AdminCanvas");
        if (existing != null)
        {
            bool rebuild = EditorUtility.DisplayDialog(
                "AdminCanvas đã tồn tại",
                "Đã có \"AdminCanvas\" trong Scene. Xoá và dựng lại từ đầu?",
                "Xoá & dựng lại",
                "Huỷ"
            );

            if (!rebuild) return;

            Undo.DestroyObjectImmediate(existing);
        }

        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        EnsureEventSystem();

        // ---- Canvas riêng cho admin panel (Screen Space - Overlay) — độc
        // lập với Canvas Screen-Space-Camera đang dùng cho camera preview,
        // luôn hiện trên cùng, không cần gán Camera/sorting order thêm. ----
        GameObject canvasGo = new GameObject("AdminCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Undo.RegisterCreatedObjectUndo(canvasGo, "Build Admin Panel UI");

        Canvas canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100; // luôn nổi trên Canvas camera-preview

        CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;

        AdminPanelController controller = canvasGo.AddComponent<AdminPanelController>();

        // ---- Nút mở admin — luôn hiện, góc dưới phải ----
        Button triggerBtn = BuildTriggerButton(canvasGo.transform);

        // ---- 4 panel ----
        GameObject pinPanel = BuildPinPanel(canvasGo.transform, controller,
            out InputField pinInput, out Text pinStatus, out Button pinConfirmBtn, out Button pinCloseBtn);

        GameObject menuPanel = BuildMenuPanel(canvasGo.transform, controller,
            out Button menuCancelBtn, out Button menuCreateBtn, out Button menuClearCacheBtn,
            out Text menuStatus, out Button menuLogoutBtn, out Button menuCloseBtn);

        GameObject cancelPanel = BuildCancelPanel(canvasGo.transform, controller,
            out Text cancelShowtimeLabel, out Button cancelShowtimePrevBtn, out Button cancelShowtimeNextBtn,
            out InputField cancelSeat, out Text cancelStatus,
            out Button cancelConfirmBtn, out Button cancelBackBtn);

        GameObject createPanel = BuildCreatePanel(canvasGo.transform, controller,
            out Text createShowtimeLabel, out Button createShowtimePrevBtn, out Button createShowtimeNextBtn,
            out InputField createSeat, out InputField createName,
            out InputField createPhone, out InputField createEmail, out Text createStatus,
            out GameObject qrContainer, out RawImage qrImage, out GameObject codeContainer, out Text codeText,
            out Button createConfirmBtn, out Button createBackBtn);

        // ---- Gắn field vào AdminPanelController qua SerializedObject
        // (field là private nên không gán trực tiếp được từ đây) ----
        SerializedObject so = new SerializedObject(controller);
        so.FindProperty("pinPanel").objectReferenceValue = pinPanel;
        so.FindProperty("menuPanel").objectReferenceValue = menuPanel;
        so.FindProperty("cancelPanel").objectReferenceValue = cancelPanel;
        so.FindProperty("createPanel").objectReferenceValue = createPanel;

        so.FindProperty("pinInput").objectReferenceValue = pinInput;
        so.FindProperty("pinStatusText").objectReferenceValue = pinStatus;

        so.FindProperty("menuStatusText").objectReferenceValue = menuStatus;

        so.FindProperty("cancelShowtimeLabel").objectReferenceValue = cancelShowtimeLabel;
        so.FindProperty("cancelSeatInput").objectReferenceValue = cancelSeat;
        so.FindProperty("cancelStatusText").objectReferenceValue = cancelStatus;

        so.FindProperty("createShowtimeLabel").objectReferenceValue = createShowtimeLabel;
        so.FindProperty("createSeatInput").objectReferenceValue = createSeat;
        so.FindProperty("createNameInput").objectReferenceValue = createName;
        so.FindProperty("createPhoneInput").objectReferenceValue = createPhone;
        so.FindProperty("createEmailInput").objectReferenceValue = createEmail;
        so.FindProperty("createStatusText").objectReferenceValue = createStatus;
        so.FindProperty("createQrContainer").objectReferenceValue = qrContainer;
        so.FindProperty("createQrImage").objectReferenceValue = qrImage;
        so.FindProperty("createCodeContainer").objectReferenceValue = codeContainer;
        so.FindProperty("createCodeText").objectReferenceValue = codeText;

        so.ApplyModifiedProperties();

        // ---- Gắn Button.onClick (persistent, hiện trong Inspector như event tay) ----
        AddListener(triggerBtn, controller.OnAdminButtonClicked);

        AddListener(pinConfirmBtn, controller.OnVerifyPinClicked);
        AddListener(pinCloseBtn, controller.OnCloseAll);

        AddListener(menuCancelBtn, controller.OnOpenCancelPanel);
        AddListener(menuCreateBtn, controller.OnOpenCreatePanel);
        AddListener(menuClearCacheBtn, controller.OnClearLocalCache);
        AddListener(menuLogoutBtn, controller.OnLogout);
        AddListener(menuCloseBtn, controller.OnCloseAll);

        AddListener(cancelShowtimePrevBtn, controller.OnCancelShowtimePrev);
        AddListener(cancelShowtimeNextBtn, controller.OnCancelShowtimeNext);
        AddListener(cancelConfirmBtn, controller.OnCancelSubmit);
        AddListener(cancelBackBtn, controller.OnBackToMenu);

        AddListener(createShowtimePrevBtn, controller.OnCreateShowtimePrev);
        AddListener(createShowtimeNextBtn, controller.OnCreateShowtimeNext);
        AddListener(createConfirmBtn, controller.OnCreateSubmit);
        AddListener(createBackBtn, controller.OnBackToMenu);

        EditorSceneManager.MarkSceneDirty(canvasGo.scene);
        Selection.activeGameObject = canvasGo;

        Debug.Log("AdminPanelUIBuilder: Đã dựng xong \"AdminCanvas\". Nhớ Ctrl+S để lưu Scene.");
    }

    private static void EnsureEventSystem()
    {
        if (Object.FindFirstObjectByType<EventSystem>() != null) return;

        GameObject es = new GameObject("EventSystem", typeof(EventSystem));
        Undo.RegisterCreatedObjectUndo(es, "Build Admin Panel UI (EventSystem)");
        Debug.LogWarning("AdminPanelUIBuilder: Không thấy EventSystem trong Scene, đã tạo mới cơ bản — " +
            "kiểm tra lại có cần thêm Input Module đúng với Input System project đang dùng không.");
    }

    private static void AddListener(Button btn, UnityAction call)
    {
        UnityEventTools.AddPersistentListener(btn.onClick, call);
    }

    // =========================================================
    // NÚT TRIGGER
    // =========================================================

    private static Button BuildTriggerButton(Transform parent)
    {
        GameObject go = NewUI("AdminTriggerBtn", parent, typeof(Image), typeof(Button));
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(1, 0);
        rt.pivot = new Vector2(1, 0);
        rt.sizeDelta = new Vector2(56, 56);
        rt.anchoredPosition = new Vector2(-16, 16);

        go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.45f);

        Text t = AddLabel(go.transform, "Admin", 14, FontStyle.Bold);
        StretchFull(t.rectTransform);

        return go.GetComponent<Button>();
    }

    // =========================================================
    // PIN PANEL
    // =========================================================

    private static GameObject BuildPinPanel(Transform parent, AdminPanelController controller,
        out InputField pinInput, out Text status, out Button confirmBtn, out Button closeBtn)
    {
        GameObject panel = BuildOverlayPanel(parent, "PinPanel", "Nhập PIN quản trị", out Transform card);

        pinInput = AddInputField(card, "Mật khẩu admin", isPassword: true);
        status = AddStatusText(card);
        confirmBtn = AddButton(card, "Xác nhận", ColorPrimary);
        closeBtn = AddButton(card, "Đóng", ColorNeutral);

        panel.SetActive(false);
        return panel;
    }

    // =========================================================
    // MENU PANEL
    // =========================================================

    private static GameObject BuildMenuPanel(Transform parent, AdminPanelController controller,
        out Button cancelBtn, out Button createBtn, out Button clearCacheBtn, out Text statusText,
        out Button logoutBtn, out Button closeBtn)
    {
        GameObject panel = BuildOverlayPanel(parent, "MenuPanel", "Quản trị vé", out Transform card);

        cancelBtn = AddButton(card, "Huỷ vé theo ghế", ColorDanger);
        createBtn = AddButton(card, "Tạo vé thủ công", ColorPrimary);
        clearCacheBtn = AddButton(card, "Xoá cache check-in cục bộ", ColorNeutral);
        statusText = AddStatusText(card);
        logoutBtn = AddButton(card, "Đăng xuất", ColorNeutral);
        closeBtn = AddButton(card, "Đóng", ColorNeutral);

        panel.SetActive(false);
        return panel;
    }

    // =========================================================
    // CANCEL PANEL
    // =========================================================

    private static GameObject BuildCancelPanel(Transform parent, AdminPanelController controller,
        out Text showtimeLabel, out Button showtimePrevBtn, out Button showtimeNextBtn,
        out InputField seatInput, out Text status,
        out Button confirmBtn, out Button backBtn)
    {
        GameObject panel = BuildOverlayPanel(parent, "CancelPanel", "Huỷ vé theo ghế", out Transform card);

        showtimeLabel = AddShowtimePicker(card, out showtimePrevBtn, out showtimeNextBtn);
        seatInput = AddInputField(card, "Mã ghế (vd B12)");
        status = AddStatusText(card);
        confirmBtn = AddButton(card, "Xác nhận huỷ", ColorDanger);
        backBtn = AddButton(card, "Quay lại", ColorNeutral);

        panel.SetActive(false);
        return panel;
    }

    // =========================================================
    // CREATE PANEL
    // =========================================================

    private static GameObject BuildCreatePanel(Transform parent, AdminPanelController controller,
        out Text showtimeLabel, out Button showtimePrevBtn, out Button showtimeNextBtn,
        out InputField seatInput, out InputField nameInput,
        out InputField phoneInput, out InputField emailInput, out Text status,
        out GameObject qrContainer, out RawImage qrImage, out GameObject codeContainer, out Text codeText,
        out Button confirmBtn, out Button backBtn)
    {
        GameObject panel = BuildOverlayPanel(parent, "CreatePanel", "Tạo vé thủ công", out Transform card);

        showtimeLabel = AddShowtimePicker(card, out showtimePrevBtn, out showtimeNextBtn);
        seatInput = AddInputField(card, "Mã ghế (vd B12)");
        nameInput = AddInputField(card, "Tên khách hàng");
        phoneInput = AddInputField(card, "Số điện thoại (tuỳ chọn)");
        phoneInput.keyboardType = TouchScreenKeyboardType.PhonePad;
        emailInput = AddInputField(card, "Email (tuỳ chọn)");
        status = AddStatusText(card);

        qrContainer = NewUI("QrResult", card, typeof(RawImage), typeof(LayoutElement));
        LayoutElement qrLe = qrContainer.GetComponent<LayoutElement>();
        qrLe.preferredWidth = 220;
        qrLe.preferredHeight = 220;
        qrImage = qrContainer.GetComponent<RawImage>();
        qrContainer.SetActive(false);

        codeContainer = NewUI("TicketCodeText", card, typeof(LayoutElement));
        codeContainer.GetComponent<LayoutElement>().preferredHeight = 30;
        codeText = codeContainer.AddComponent<Text>();
        codeText.font = uiFont;
        codeText.fontSize = 16;
        codeText.fontStyle = FontStyle.Bold;
        codeText.alignment = TextAnchor.MiddleCenter;
        codeText.color = Color.white;
        codeContainer.SetActive(false);

        confirmBtn = AddButton(card, "Tạo vé", ColorPrimary);
        backBtn = AddButton(card, "Quay lại", ColorNeutral);

        panel.SetActive(false);
        return panel;
    }

    // =========================================================
    // HẠ TẦNG DỰNG UI DÙNG CHUNG
    // =========================================================

    private static GameObject NewUI(string name, Transform parent, params System.Type[] extraComponents)
    {
        System.Type[] comps = new System.Type[extraComponents.Length + 1];
        comps[0] = typeof(RectTransform);
        extraComponents.CopyTo(comps, 1);

        GameObject go = new GameObject(name, comps);
        go.GetComponent<RectTransform>().SetParent(parent, false);
        return go;
    }

    private static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private static Text AddLabel(Transform parent, string text, int fontSize, FontStyle style)
    {
        GameObject go = NewUI("Label", parent);
        Text t = go.AddComponent<Text>();
        t.font = uiFont;
        t.text = text;
        t.fontSize = fontSize;
        t.fontStyle = style;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.raycastTarget = false;
        return t;
    }

    /// <summary>Overlay mờ toàn màn hình + Card giữa màn hình (VerticalLayoutGroup), kèm tiêu đề.</summary>
    private static GameObject BuildOverlayPanel(Transform parent, string name, string title, out Transform card)
    {
        GameObject panel = NewUI(name, parent, typeof(Image));
        StretchFull(panel.GetComponent<RectTransform>());
        panel.GetComponent<Image>().color = new Color(0, 0, 0, 0.75f);

        GameObject cardGo = NewUI("Card", panel.transform, typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        RectTransform cardRt = cardGo.GetComponent<RectTransform>();
        cardRt.anchorMin = cardRt.anchorMax = new Vector2(0.5f, 0.5f);
        cardRt.pivot = new Vector2(0.5f, 0.5f);
        cardRt.sizeDelta = new Vector2(520, 0);
        cardGo.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f, 0.97f);

        VerticalLayoutGroup vlg = cardGo.GetComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(28, 28, 24, 24);
        vlg.spacing = 12;
        vlg.childAlignment = TextAnchor.UpperCenter;
        vlg.childControlWidth = true;
        vlg.childForceExpandWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandHeight = false;

        ContentSizeFitter fitter = cardGo.GetComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        GameObject titleGo = NewUI("Title", cardGo.transform, typeof(LayoutElement));
        titleGo.GetComponent<LayoutElement>().preferredHeight = 36;
        Text titleText = titleGo.AddComponent<Text>();
        titleText.font = uiFont;
        titleText.text = title;
        titleText.fontSize = 24;
        titleText.fontStyle = FontStyle.Bold;
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.color = Color.white;
        titleText.raycastTarget = false;

        card = cardGo.transform;
        return panel;
    }

    /// <summary>
    /// Hàng chọn suất diễn: nút "&lt;" — nhãn tên suất — nút "&gt;". Danh sách
    /// suất lấy thật từ backend (AdminPanelController.FetchShowtimes) khi mở
    /// panel; bấm "&lt;"/"&gt;" chỉ lướt trong danh sách đã tải sẵn, không gọi
    /// mạng thêm mỗi lần bấm. Dùng ký tự "&lt;"/"&gt;" thường thay vì mũi tên
    /// Unicode — an toàn với mọi font, giống lý do đổi icon bánh răng của nút
    /// Admin trước đó.
    /// </summary>
    private static Text AddShowtimePicker(Transform parent, out Button prevBtn, out Button nextBtn)
    {
        GameObject row = NewUI("ShowtimePicker", parent, typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.GetComponent<LayoutElement>().preferredHeight = 44;

        HorizontalLayoutGroup hlg = row.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8;
        hlg.childAlignment = TextAnchor.MiddleCenter;
        hlg.childControlWidth = true;
        hlg.childForceExpandWidth = false;
        hlg.childControlHeight = true;
        hlg.childForceExpandHeight = true;

        GameObject prevGo = NewUI("PrevBtn", row.transform, typeof(Image), typeof(Button), typeof(LayoutElement));
        prevGo.GetComponent<LayoutElement>().preferredWidth = 44;
        prevGo.GetComponent<Image>().color = ColorNeutral;
        Text prevText = AddLabel(prevGo.transform, "<", 20, FontStyle.Bold);
        StretchFull(prevText.rectTransform);
        prevBtn = prevGo.GetComponent<Button>();

        GameObject labelGo = NewUI("Label", row.transform, typeof(LayoutElement));
        labelGo.GetComponent<LayoutElement>().flexibleWidth = 1;
        Text label = labelGo.AddComponent<Text>();
        label.font = uiFont;
        label.fontSize = 14;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.white;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.text = "Đang tải suất diễn...";

        GameObject nextGo = NewUI("NextBtn", row.transform, typeof(Image), typeof(Button), typeof(LayoutElement));
        nextGo.GetComponent<LayoutElement>().preferredWidth = 44;
        nextGo.GetComponent<Image>().color = ColorNeutral;
        Text nextText = AddLabel(nextGo.transform, ">", 20, FontStyle.Bold);
        StretchFull(nextText.rectTransform);
        nextBtn = nextGo.GetComponent<Button>();

        return label;
    }

    private static Text AddStatusText(Transform parent)
    {
        GameObject go = NewUI("Status", parent, typeof(LayoutElement));
        go.GetComponent<LayoutElement>().preferredHeight = 44;

        Text t = go.AddComponent<Text>();
        t.font = uiFont;
        t.fontSize = 15;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = new Color(1f, 0.85f, 0.4f);
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.raycastTarget = false;
        return t;
    }

    private static InputField AddInputField(Transform parent, string placeholderText, bool isPassword = false)
    {
        GameObject go = NewUI("Input_" + placeholderText, parent, typeof(Image), typeof(InputField), typeof(LayoutElement));
        go.GetComponent<LayoutElement>().preferredHeight = 44;
        go.GetComponent<Image>().color = new Color(1, 1, 1, 0.08f);

        InputField input = go.GetComponent<InputField>();

        if (isPassword)
        {
            input.contentType = InputField.ContentType.Password;
            input.keyboardType = TouchScreenKeyboardType.NumberPad;
        }

        GameObject textGo = NewUI("Text", go.transform);
        StretchFull(textGo.GetComponent<RectTransform>());
        textGo.GetComponent<RectTransform>().offsetMin = new Vector2(10, 4);
        textGo.GetComponent<RectTransform>().offsetMax = new Vector2(-10, -4);
        Text txt = textGo.AddComponent<Text>();
        txt.font = uiFont;
        txt.fontSize = 17;
        txt.color = Color.white;
        txt.alignment = TextAnchor.MiddleLeft;
        txt.supportRichText = false;

        GameObject placeGo = NewUI("Placeholder", go.transform);
        StretchFull(placeGo.GetComponent<RectTransform>());
        placeGo.GetComponent<RectTransform>().offsetMin = new Vector2(10, 4);
        placeGo.GetComponent<RectTransform>().offsetMax = new Vector2(-10, -4);
        Text place = placeGo.AddComponent<Text>();
        place.font = uiFont;
        place.fontSize = 17;
        place.fontStyle = FontStyle.Italic;
        place.color = new Color(1, 1, 1, 0.4f);
        place.text = placeholderText;
        place.alignment = TextAnchor.MiddleLeft;

        input.textComponent = txt;
        input.placeholder = place;

        return input;
    }

    private static Button AddButton(Transform parent, string label, Color color)
    {
        GameObject go = NewUI("Btn_" + label, parent, typeof(Image), typeof(Button), typeof(LayoutElement));
        go.GetComponent<LayoutElement>().preferredHeight = 46;
        go.GetComponent<Image>().color = color;

        Text t = AddLabel(go.transform, label, 17, FontStyle.Bold);
        StretchFull(t.rectTransform);

        return go.GetComponent<Button>();
    }
}
