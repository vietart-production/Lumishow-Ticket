using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Cache CỤC BỘ trên máy quét (KHÔNG phải Firestore) các mã vé đã được app này xác nhận check-in
/// thành công - dùng để CHẶN việc quét lại 1 mã QR nhiều lần (khách đứng lâu trước camera, QR bị
/// quét trùng do rung/mất khung rồi bắt lại, hoặc nhân viên lỡ quét lại vé cũ) phải tốn 1 lượt đọc
/// Firestore mỗi lần trong khi kết quả luôn là "đã check-in" và không đổi - đúng tinh thần tránh
/// quét/ghi đè tập thể gây lãng phí quota đã thống nhất từ đầu.
///
/// Lưu tại Application.persistentDataPath (đường dẫn ghi dữ liệu người dùng MẶC ĐỊNH, chuẩn của
/// Unity, tồn tại xuyên suốt giữa các lần mở app trên mọi nền tảng) dưới dạng 1 file JSON phẳng.
///
/// QUAN TRỌNG: đây CHỈ là 1 lớp tối ưu chi phí đọc, không phải nguồn sự thật - trường `checkedIn`
/// trên Firestore vẫn luôn là nơi quyết định 1 vé đã được dùng hay chưa. Cache này có thể xoá bất
/// kỳ lúc nào (Admin Panel > "Xoá cache check-in cục bộ") mà không ảnh hưởng tính đúng đắn của hệ
/// thống - xoá xong chỉ khiến các mã đã check-in phải tra lại Firestore 1 lần nữa (vẫn sẽ bị từ
/// chối đúng như cũ), không có rủi ro cho khách hàng chưa check-in tự nhiên được vào.
/// </summary>
public static class LocalCheckInCache
{
    private const string FileName = "checked_in_tickets.json";

    private static HashSet<string> cachedCodes;

    private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

    [Serializable]
    private class CheckedInCodesData
    {
        public List<string> codes = new List<string>();
    }

    private static void EnsureLoaded()
    {
        if (cachedCodes != null)
            return;

        cachedCodes = new HashSet<string>();

        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                CheckedInCodesData data = JsonUtility.FromJson<CheckedInCodesData>(json);

                if (data != null && data.codes != null)
                {
                    foreach (string code in data.codes)
                    {
                        cachedCodes.Add(code);
                    }
                }

                Debug.Log("LocalCheckInCache: Đã nạp " + cachedCodes.Count + " mã từ " + FilePath);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("LocalCheckInCache: Lỗi khi đọc file cache - " + e.Message);
            cachedCodes = new HashSet<string>();
        }
    }

    /// <summary>
    /// True nếu mã vé này đã từng được xác nhận check-in (thành công hoặc đã check-in từ trước)
    /// qua chính máy này - dùng để bỏ qua truy vấn Firestore không cần thiết.
    /// </summary>
    public static bool Contains(string ticketCode)
    {
        if (string.IsNullOrEmpty(ticketCode))
            return false;

        EnsureLoaded();
        return cachedCodes.Contains(ticketCode);
    }

    /// <summary>
    /// Đánh dấu 1 mã vé đã check-in vào cache rồi lưu ngay xuống file. Ghi ngay (không gộp/trễ)
    /// vì quét vé diễn ra ở tốc độ con người - ghi file mỗi lần không đáng kể về hiệu năng, và
    /// tránh mất cache nếu app bị tắt đột ngột giữa ca làm việc.
    /// </summary>
    public static void Add(string ticketCode)
    {
        if (string.IsNullOrEmpty(ticketCode))
            return;

        EnsureLoaded();

        if (!cachedCodes.Add(ticketCode))
            return; // Đã có sẵn trong cache từ trước, không cần ghi lại file.

        Save();
    }

    /// <summary>
    /// Xoá trống toàn bộ cache (cả trong bộ nhớ lẫn file trên máy) - gọi từ Admin Panel khi cần
    /// (vd nghi ngờ cache sai, hoặc muốn buộc quét lại tra thẳng Firestore). Không đụng gì tới dữ
    /// liệu trên Firestore - xem class doc phía trên.
    /// </summary>
    public static int Clear()
    {
        EnsureLoaded();
        int previousCount = cachedCodes.Count;

        cachedCodes = new HashSet<string>();
        Save();

        return previousCount;
    }

    private static void Save()
    {
        try
        {
            CheckedInCodesData data = new CheckedInCodesData
            {
                codes = new List<string>(cachedCodes)
            };

            File.WriteAllText(FilePath, JsonUtility.ToJson(data));
        }
        catch (Exception e)
        {
            Debug.LogError("LocalCheckInCache: Lỗi khi ghi file cache - " + e.Message);
        }
    }
}
