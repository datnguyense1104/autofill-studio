# Phase 5 — UI tạo recipe (khách tự làm)

Status: DONE (2026-10-16) — chạy thử trên trình duyệt thật (không AI): tự đánh dấu mẫu Word (bôi chọn → chỗ cần điền, chọn nhiều đoạn → phần thân), lưu v1/v2, mở lại công thức; mẫu B210 thật: ô lẻ + luật dòng (334 dòng). Khác kế hoạch ban đầu:
- Trang riêng `recipe.html` (không modal); file: `recipe-builder.js`, `word-preview.js`, `excel-grid.js`, `slot-editor.js`, `slots.js` (helper thuần, có test node), `strings.js` (chuỗi giao diện builder/run/review), `tour.js`, `jobs.js`. API trong `Web/Recipes.cs`, `Web/Templates.cs` (không có thư mục `Endpoints/`).
- "Chạy thử" = lưu rồi mở trang chính với công thức chọn sẵn (lượt chạy dùng phiên bản vừa lưu), không có chế độ chạy thử không lưu.
- Excel: chưa có nhập CSV ánh xạ (B210 dùng ô lẻ + luật dòng thay thế). "Dùng khi nào" là một ô nhập (gợi ý: ai gửi / về việc gì / khác gì), không tách 3 ô.
- Thêm: "Kiểm tra thêm sau khi điền" (dateAfter / sameSentenceCount / regex), vai trò reviewer không sửa được công thức (403).
- Chưa có E2E Playwright tự động: kiểm thử UI bằng `node --test` (helper) + chạy tay bằng agent-browser.

UI viết bằng HTML/CSS/JS module thuần như app SNV (không framework), gọi `/api/*`.

## Luồng
1. **Tải mẫu** (.docx/.doc/.xlsx/.xls), tùy chọn thêm 1–3 văn bản đã điền cùng mẫu → modal loading (nút "Tự điền, không chờ AI").
2. **AI phân tích** (phase 3) → đề xuất: tên recipe, "dùng khi nào" (ai gửi / về việc gì / phân biệt với recipe gần giống), danh sách slot + strategy + hướng dẫn. Ô do AI điền được tô nhẹ để soát.
3. **Soát slot trên bản xem trước**:
   - Word: xem văn bản dạng HTML, chỗ cần điền tô màu; bấm để sửa/xóa; bôi chọn chữ → "Thêm chỗ cần điền"; kéo chọn đoạn → "Đặt làm phần thân".
   - Excel: lưới sheet; ô cần điền tô màu; chọn ô / tạo luật dòng; nhập CSV ánh xạ.
   - Bảng slot bên phải: nhãn, kiểu, strategy, cấu hình (ngôn ngữ đích, file tra cứu + cột khóa + cột giá trị, công thức), bắt buộc.
4. **Chạy thử** trên 1 bộ nguồn → xem như màn hình duyệt → quay lại sửa.
5. **Lưu** → `recipe_version` mới (bản cũ giữ, run cũ trỏ đúng version).

## Yêu cầu UX (bài học app SNV)
- Modal chỉ đóng bằng Hủy/✕; ô nhập tự giãn hiển thị đủ nội dung; guided tour lần đầu; header + menu người dùng như app SNV.
- Tiếng Việt mặc định, chuỗi giao diện gom 1 file để thêm tiếng Anh sau.

## Files
`src/AutoFill.Web/wwwroot/{recipes-pane.js,recipe-builder.js,word-preview.js,excel-grid.js,tour.js}`, API `src/AutoFill.Web/Endpoints/RecipeEndpoints.cs`

## Kiểm thử
- `node --test` cho helper thuần (diff, ghép "dùng khi nào", chuyển chọn chữ → offset).
- E2E Playwright: tạo recipe SFDR từ đầu chỉ bằng UI.
