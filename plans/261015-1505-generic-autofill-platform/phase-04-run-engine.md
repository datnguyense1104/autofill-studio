# Phase 4 — Engine chạy recipe

Status: DONE phần code (2026-10-16) — 60 test .NET + 2 test UI pass. Còn chờ: chạy tích hợp có key với 3 bộ file thật. Khác kế hoạch ban đầu:
- Engine nằm ở `src/AutoFill.Documents/Runs/` (không phải `AutoFill.Core/Runs`): Core không tham chiếu được Slot/SourceDocument. File: `RunEngine`, `AiFiller`, `RuleStrategies`, `Verifier`, `RecipeSuggester`, `RunModels`.
- Thứ tự: constant → lookup → AI (extract/generate/translate) → lookup theo ô AI → dịch từ ô khác (`fromSlot`) → formula (sau AI vì có thể dùng giá trị AI, tự xếp thứ tự phụ thuộc) → kiểm tra → luật recipe → "còn thiếu gì".
- Gom lượt: theo `Group`, 10 ô/lượt; luật dòng 40 dòng/lượt và mọi cột cùng nhóm đi chung (B210 G/I/L, SFDR NL/FR cùng một call → nhất quán). Song song tối đa 3. Nguồn đặt đầu prompt để provider cache prompt.
- Cấu hình strategy (`StrategyConfig`): constant `{value}`; lookup `{keyColumn, valueColumn, sheet?, source?, keyFrom?, key?}` (luật dòng tra theo nhãn dòng); formula `{expression: "{s1} * {s2}"}` (số → tính, chữ → ghép); translate `{language, fromSlot?}` (không có fromSlot = dịch nhãn dòng).
- Kiểm tra bằng code: kiểu (số, %, ngày dd/MM/yyyy, boolean/enum ∈ options); trích dẫn phải có trong nguồn (tự sửa trang nếu AI ghi sai trang); mọi con số của ô extract phải có trong nguồn; độ tin cậy < 0.6; thiếu thông tin bắt buộc. Luật recipe: `dateAfter`, `sameSentenceCount`, `regex`.
- "Còn thiếu gì": chỉ giữ mục có trích dẫn tìm thấy trong nguồn (mục AI bịa bị bỏ). Bật mặc định cho mẫu Word.
- Chưa làm: few-shot từ `recipe_examples` (giá trị mẫu cũ/đoạn thân mẫu cũ đã đóng vai trò văn phong); chi phí tiền (chỉ ghi token: `ai_logs.job_id` → tổng theo lượt chạy).
- Bảng mới: `recipes`, `recipe_versions` (bất biến; sửa = phiên bản mới, lượt chạy cũ giữ phiên bản của nó), `runs`, `run_values` (có sẵn cột `reviewed_value`, `reviewed` cho phase 6).
- API: `GET/POST /api/recipes`, `GET /api/recipes/{id}`, `POST /api/recipes/{id}/versions`, `POST /api/recipes/suggest` (job `recipe.suggest`), `POST /api/runs` (job `run.execute`), `GET /api/runs`, `GET /api/runs/{id}` (job lỗi → status `failed` + lý do).
- Hạ tầng test: một instance Postgres "gốc" khởi tạo một lần rồi chép cho mỗi lớp test (khởi động 18 s → ~2 s); cả bộ 175 s → ~50 s.

## Luồng 1 lượt chạy (job nền)
1. Ingest nguồn (phase 2, có cache).
2. Lấy slots của recipe version đang dùng.
3. Giải `lookup` / `constant` / `formula` trước (không AI).
4. Gom slots AI theo `Group` thành batch (mặc định 10 slot/batch), chạy song song có giới hạn (SemaphoreSlim, mặc định 3).
5. Mỗi kết quả AI theo JSON schema strict: `{ slotId, value, evidence:[{fileId,page,quote}], confidence, note }`.
   - `translate` nhiều ngôn ngữ: 1 call trả mọi ngôn ngữ để nhất quán (bài học SFDR).
   - `generate` cho `section`: kèm đoạn mẫu + 1–3 ví dụ của recipe làm few-shot.
6. Kiểm tra (verification):
   - Luật cứng theo ValueType: số/percent/date parse được; boolean ∈ tập cho phép; số không bị dịch; enum hợp lệ.
   - Bằng chứng: `quote` phải tìm thấy (gần đúng) trong nguồn, nếu không → cảnh báo "không có căn cứ".
   - Lượt AI độc lập "còn thiếu gì": liệt kê thông tin quan trọng trong nguồn chưa xuất hiện trong kết quả (bài học SNV) → người duyệt chọn Bổ sung / Không cần.
   - Luật tùy chỉnh của recipe (vd "ngày phải sau ngày văn bản đến", "2 ngôn ngữ cùng số câu").
7. Lưu `run_values`, trạng thái `needs_review`; thông báo UI.

## Khác
- Chọn recipe tự động: AI so nguồn với mô tả "dùng khi nào" của các recipe trong workspace → đề xuất top 3 + lý do; người dùng xác nhận.
- Ước tính và ghi chi phí mỗi run.
- Prompt chung + phần riêng của recipe; không hard-code nghiệp vụ SNV/SFDR trong code.

## Files
`src/AutoFill.Core/Runs/{RunPipeline,SlotResolver,Strategies/*,Verification/*}.cs`, `src/AutoFill.Ai/Prompts/*`

## Kiểm thử
- Unit: từng strategy với AI giả (fake `IAiClient` chỉ trong test).
- Luật cứng: "0.05" giữ nguyên, "Y" giữ nguyên, ngày sai định dạng bị chặn.
- Tích hợp (chạy tay, có key): 3 bộ file mẫu cho ra kết quả, thời gian và chi phí được ghi log.
