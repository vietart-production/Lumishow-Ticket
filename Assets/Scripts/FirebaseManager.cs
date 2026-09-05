using System;
using System.Collections.Generic;
using UnityEngine;
using Firebase;
using Firebase.Auth;
using Firebase.Extensions;
using Firebase.Firestore;

/// <summary>
/// Kết quả trả về sau khi kiểm tra / check-in 1 vé.
/// </summary>
public class TicketCheckInResult
{
    public bool Success;

    // True khi vé TỒN TẠI nhưng đã checkedIn=true từ trước (không phải lỗi hệ thống/mạng) - tách
    // riêng khỏi Success để TicketManager biết CHẮC CHẮN khi nào nên thêm mã vào
    // LocalCheckInCache (cả lúc check-in thành công LẪN lúc phát hiện đã check-in từ trước), phân
    // biệt với các lỗi tạm thời (mất mạng, vé không tồn tại) không nên cache.
    public bool AlreadyCheckedIn;

    public string Message;
    public DocumentSnapshot Snapshot; // null nếu không tìm thấy vé
}

public class FirebaseManager : MonoBehaviour
{
    public static FirebaseManager Instance;

    private FirebaseApp firebaseApp;
    private FirebaseFirestore db;
    private FirebaseAuth auth;

    // Firestore Security Rules chỉ cho phép client đã đăng nhập đọc/check-in
    // vé (xem firestore.rules bên repo web) — nên phải chờ cả Firestore lẫn
    // đăng nhập ẩn danh xong mới coi là sẵn sàng, không chỉ riêng db != null.
    public bool IsReady => db != null && auth != null && auth.CurrentUser != null;

    private void Awake()
    {
        Instance = this;
    }

    private void Start()
    {
        CheckFirebase();
    }

    private void CheckFirebase()
    {
        FirebaseApp.CheckAndFixDependenciesAsync()
            .ContinueWithOnMainThread(task =>
            {
                var dependencyStatus = task.Result;

                if (dependencyStatus == DependencyStatus.Available)
                {
                    firebaseApp = FirebaseApp.DefaultInstance;
                    db = FirebaseFirestore.DefaultInstance;
                    auth = FirebaseAuth.DefaultInstance;

                    Debug.Log("=================================");
                    Debug.Log("FIREBASE INITIALIZED SUCCESSFULLY");
                    Debug.Log("Project: " + firebaseApp.Options.ProjectId);
                    Debug.Log("=================================");

                    SignInAnonymously();
                }
                else
                {
                    Debug.LogError(
                        "Firebase initialization failed: " +
                        dependencyStatus
                    );
                }
            });
    }

    /// <summary>
    /// Đăng nhập ẩn danh — không cần màn hình đăng nhập, chỉ để Firestore
    /// Security Rules phân biệt được "app soát vé thật" (request.auth != null)
    /// với truy cập ẩn danh hoàn toàn (bị chặn). Phù hợp thiết bị dùng chung
    /// tại cổng, không cần tài khoản riêng từng nhân viên.
    /// </summary>
    private void SignInAnonymously()
    {
        auth.SignInAnonymouslyAsync().ContinueWithOnMainThread(task =>
        {
            if (task.IsCanceled || task.IsFaulted)
            {
                Debug.LogError("Đăng nhập ẩn danh thất bại: " + task.Exception);
                return;
            }

            AuthResult result = task.Result;
            Debug.Log("Đã đăng nhập ẩn danh, uid: " + result.User.UserId);
        });
    }

    /// <summary>
    /// Tìm vé theo field "ticketCode". Nếu tồn tại và chưa checkedIn thì set
    /// checkedIn = true + ghi checkedInAt. Kết quả trả về qua callback
    /// (đã được ContinueWithOnMainThread nên gọi thẳng UI/Unity API được).
    /// </summary>
    public void CheckInTicket(string ticketCode, Action<TicketCheckInResult> onComplete)
    {
        if (!IsReady)
        {
            onComplete?.Invoke(new TicketCheckInResult
            {
                Success = false,
                Message = "Firebase chưa sẵn sàng, thử lại sau"
            });
            return;
        }

        db.Collection("tickets")
            .WhereEqualTo("ticketCode", ticketCode)
            .Limit(1)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    Debug.LogError("Lỗi truy vấn ticket: " + task.Exception);
                    onComplete?.Invoke(new TicketCheckInResult
                    {
                        Success = false,
                        Message = "Lỗi kết nối, thử lại"
                    });
                    return;
                }

                QuerySnapshot snapshot = task.Result;

                if (snapshot.Count == 0)
                {
                    onComplete?.Invoke(new TicketCheckInResult
                    {
                        Success = false,
                        Message = "Vé không tồn tại"
                    });
                    return;
                }

                DocumentSnapshot ticketDoc = null;
                foreach (DocumentSnapshot doc in snapshot.Documents)
                {
                    ticketDoc = doc;
                    break;
                }

                bool alreadyCheckedIn =
                    ticketDoc.ContainsField("checkedIn") &&
                    ticketDoc.GetValue<bool>("checkedIn");

                if (alreadyCheckedIn)
                {
                    onComplete?.Invoke(new TicketCheckInResult
                    {
                        Success = false,
                        AlreadyCheckedIn = true,
                        Message = "Vé đã được check-in trước đó",
                        Snapshot = ticketDoc
                    });
                    return;
                }

                var updates = new Dictionary<string, object>
                {
                    { "checkedIn", true },
                    { "checkedInAt", Timestamp.GetCurrentTimestamp() }
                };

                ticketDoc.Reference.UpdateAsync(updates)
                    .ContinueWithOnMainThread(updateTask =>
                    {
                        if (updateTask.IsFaulted || updateTask.IsCanceled)
                        {
                            Debug.LogError("Lỗi cập nhật check-in: " + updateTask.Exception);
                            onComplete?.Invoke(new TicketCheckInResult
                            {
                                Success = false,
                                Message = "Lỗi khi cập nhật, thử lại",
                                Snapshot = ticketDoc
                            });
                            return;
                        }

                        string customerName = ticketDoc.ContainsField("customerName")
                            ? ticketDoc.GetValue<string>("customerName")
                            : "";

                        Debug.Log("Check-in thành công: " + ticketCode + " - " + customerName);

                        onComplete?.Invoke(new TicketCheckInResult
                        {
                            Success = true,
                            Message = "Hợp lệ - " + customerName,
                            Snapshot = ticketDoc
                        });
                    });
            });
    }

    /// <summary>
    /// Chỉ dùng để TEST: set checkedIn về false cho 1 mã vé để có thể quét lại
    /// nhiều lần trong lúc phát triển/demo. Không gọi hàm này ở bản production.
    /// </summary>
    public void ResetTicketCheckIn(string ticketCode, Action<bool, string> onComplete)
    {
        if (!IsReady)
        {
            onComplete?.Invoke(false, "Firebase chưa sẵn sàng");
            return;
        }

        db.Collection("tickets")
            .WhereEqualTo("ticketCode", ticketCode)
            .Limit(1)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    Debug.LogError("Lỗi truy vấn ticket để reset: " + task.Exception);
                    onComplete?.Invoke(false, "Lỗi kết nối");
                    return;
                }

                QuerySnapshot snapshot = task.Result;

                if (snapshot.Count == 0)
                {
                    onComplete?.Invoke(false, "Không tìm thấy vé với mã: " + ticketCode);
                    return;
                }

                DocumentSnapshot ticketDoc = null;
                foreach (DocumentSnapshot doc in snapshot.Documents)
                {
                    ticketDoc = doc;
                    break;
                }

                var updates = new Dictionary<string, object>
                {
                    { "checkedIn", false },
                    { "checkedInAt", null }
                };

                ticketDoc.Reference.UpdateAsync(updates)
                    .ContinueWithOnMainThread(updateTask =>
                    {
                        if (updateTask.IsFaulted || updateTask.IsCanceled)
                        {
                            Debug.LogError("Lỗi reset check-in: " + updateTask.Exception);
                            onComplete?.Invoke(false, "Lỗi khi reset");
                            return;
                        }

                        Debug.Log("Đã reset checkedIn = false cho vé: " + ticketCode);
                        onComplete?.Invoke(true, "Reset thành công: " + ticketCode);
                    });
            });
    }
}