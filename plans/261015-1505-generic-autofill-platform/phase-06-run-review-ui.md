# Phase 6 — UI chạy, duyệt, xuất

Status: DONE (2026-10-16) — chạy thử trên trình duyệt thật: thả nguồn → đọc → chọn công thức → điền → duyệt → sửa → xuất .docx (tên file tiếng Việt), lượt chạy chuyển "Đã xuất". 62 test .NET + 8 test UI pass. Khác kế hoạch ban đầu:
- Trang chính (`run-pane.js`): nhiều file, đọc nền, gợi ý top 3 + lý do, lịch sử 20 lượt. Màn hình duyệt `run.html` + `review-view.js`.
- Xem trước kết quả: Word dựng lại từ outline + giá trị (cùng luật với bộ ghi: thay cụm, dòng danh sách thêm/bớt, phần thân); Excel là bảng "ô → giá trị" (không vẽ lại lưới định dạng).
- Bằng chứng: bấm mở trang nguồn (văn bản đã đọc, tô câu trích), không mở PDF gốc.
- "Bổ sung" mục còn thiếu = AI viết lại ô người dùng chọn kèm thông tin thiếu (`POST /api/runs/{id}/values/{slot}/revise`); "AI viết lại" dùng được cho mọi ô.
- Xuất: chặn khi ô bắt buộc trống (server cũng chặn); cảnh báo chưa "Đã xem" chỉ cần xác nhận. File tải qua server sau khi kiểm quyền (không dùng signed URL) — đủ cho file mẫu cỡ vài MB.
- Chưa làm: "Dùng làm ví dụ cho recipe" (cần bảng ví dụ + few-shot, đã hoãn từ phase 4).

## Luồng
1. Kéo thả nguồn (nhiều file) → upload thẳng lên file store (không giới hạn 4.5MB kiểu Vercel).
2. Hệ thống đề xuất recipe (top 3 + lý do) hoặc người dùng chọn.
3. Tiến độ realtime: Đọc nguồn → AI điền → Kiểm tra → Xong (thời gian đã chạy).
4. **Màn hình duyệt**:
   - Trái: bản xem trước file kết quả (Word/Excel), chỗ đã điền tô vàng, chỗ cảnh báo tô đỏ.
   - Phải: danh sách slot — giá trị (sửa được), bằng chứng (bấm để mở trang nguồn tại câu trích), độ tin cậy, cảnh báo.
   - Mục "Có thể còn thiếu": Bổ sung (AI viết lại phần liên quan) / Không cần.
   - Nút xuất bị khóa khi còn lỗi chặn; cảnh báo thường chỉ cần xác nhận.
5. Xuất .docx/.xlsx; lưu lịch sử + giá trị người sửa.
6. Sửa của người duyệt được lưu làm ví dụ cho recipe (người dùng chọn "Dùng làm ví dụ").

## Files
`src/AutoFill.Web/wwwroot/{run-pane.js,review-view.js}` (tiến độ qua SSE), `src/AutoFill.Web/Endpoints/RunEndpoints.cs`; tải file qua signed URL ngắn hạn sau khi kiểm quyền.

## Kiểm thử
- E2E: chạy recipe SNV trên 1 văn bản đến, sửa 1 ô, xuất, mở file kiểm tra giá trị đã sửa.
