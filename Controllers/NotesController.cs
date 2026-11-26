using Microsoft.AspNetCore.Mvc;
using Firebase.Database;
using Firebase.Database.Query;
using Firebase.Storage;
using UglyToad.PdfPig;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace HacettepeNot.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class NotesController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly HttpClient _httpClient = new HttpClient();

        // sil tekrar - Realtime DB Bağlantısı
        private readonly FirebaseClient _firebaseClient = new("https://hacettepenot-be247-default-rtdb.firebaseio.com/");
        private const string FirebaseRtdbBaseUrl = "https://hacettepenot-be247-default-rtdb.firebaseio.com";

        // sil tekrar - Güncel Firebase Storage Bucket Adresi
        private const string FirebaseBucket = "hacettepenot-be247.firebasestorage.app";

        public NotesController(IWebHostEnvironment env)
        {
            _env = env;
        }

        // sil tekrar - Sınıf bilgisini 1_sinif / 2_sinif anahtarına normalize eden yardımcı
        private static string NormalizeGradeKey(string? grade)
        {
            var cleaned = (grade ?? string.Empty).Trim().ToLowerInvariant();
            return cleaned.StartsWith("2") ? "2_sinif" : "1_sinif";
        }

        // ==========================================
        // 1. TÜM NOTLARI GETİR (GET: /api/notes)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetNotes([FromQuery] string? department = null, [FromQuery] string? grade = null)
        {
            try
            {
                Console.WriteLine("--> Notları listeleme isteği alındı...");

                var notes = await _firebaseClient
                    .Child("notes")
                    .OnceAsync<NoteRecordDto>();

                var query = notes.Select(item => new
                {
                    Key = item.Key,
                    Id = item.Object.Id,
                    CourseCode = item.Object.CourseCode,
                    ContentType = item.Object.ContentType,
                    Title = item.Object.Title,
                    Uploader = item.Object.Uploader,
                    FilePath = item.Object.FilePath,
                    Department = item.Object.Department,
                    Grade = item.Object.Grade,
                    CreatedAt = item.Object.CreatedAt
                });

                if (!string.IsNullOrWhiteSpace(department))
                {
                    query = query.Where(x => string.Equals(x.Department, department.Trim(), StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(grade))
                {
                    var normalizedGrade = NormalizeGradeKey(grade);
                    query = query.Where(x => NormalizeGradeKey(x.Grade) == normalizedGrade);
                }

                var noteList = query.OrderByDescending(x => x.CreatedAt).ToList();

                Console.WriteLine($"--> ✅ Toplam {noteList.Count} adet not başarıyla getirildi.");

                return Ok(new { Success = true, Data = noteList });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"--> ❌ LİSTELEME HATASI: {ex.Message}");
                return StatusCode(500, new { Success = false, Message = "Notlar getirilirken hata oluştu: " + ex.Message });
            }
        }

        // ==========================================
        // 2. YENİ NOT YÜKLE (POST: /api/notes/upload)
        // ==========================================
        [HttpPost("upload")]
        public async Task<IActionResult> UploadNote([FromForm] NoteUploadDto request)
        {
            Console.WriteLine("==========================================");
            Console.WriteLine("==> /api/notes/upload ENDPOINTINE İSTEK GELDİ! <==");
            Console.WriteLine($"==> Başlık: {request.Title}, Ders: {request.CourseCode}, Bölüm: {request.Department}, Sınıf: {request.Grade}");

            if (request.File == null || request.File.Length == 0)
            {
                return BadRequest(new { Success = false, Message = "Lütfen geçerli bir dosya seçin. Dosya null geldi." });
            }

            var extension = Path.GetExtension(request.File.FileName).ToLowerInvariant();
            if (extension != ".pdf")
            {
                return BadRequest(new { Success = false, Message = "Güvenlik İhlali: Sadece .pdf uzantılı dosyalar kabul edilir." });
            }

            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            string downloadUrl = string.Empty;

            try
            {
                Console.WriteLine("--> Firebase Storage'a yükleme başlatılıyor...");
                using var stream = request.File.OpenReadStream();

                var task = new FirebaseStorage(FirebaseBucket, new FirebaseStorageOptions
                    {
                        ThrowOnCancel = true
                    })
                    .Child("uploads")
                    .Child(uniqueFileName)
                    .PutAsync(stream);

                downloadUrl = await task;
                Console.WriteLine($"--> ✅ Storage yükleme BAŞARILI. URL: {downloadUrl}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"--> ❌ STORAGE HATASI: {ex}");
                return StatusCode(500, new { Success = false, Message = "Firebase Storage yükleme hatası: " + ex.Message });
            }

            var normalizedGrade = NormalizeGradeKey(request.Grade);

            var newNoteRecord = new NoteRecordDto
            {
                Id = Guid.NewGuid().ToString(),
                CourseCode = request.CourseCode,
                ContentType = request.ContentType,
                Title = request.Title,
                Uploader = request.Uploader,
                FilePath = downloadUrl,
                Department = request.Department ?? string.Empty,
                Grade = normalizedGrade,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                Console.WriteLine("--> Realtime Database'e kayıt atılıyor...");
                await _firebaseClient
                    .Child("notes")
                    .PostAsync(newNoteRecord);
                Console.WriteLine("--> ✅ Database kayıt BAŞARILI.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"--> ❌ DATABASE HATASI: {ex}");
                return StatusCode(500, new { Success = false, Message = "Firebase Veritabanı kayıt hatası: " + ex.Message });
            }

            return Ok(new
            {
                Success = true,
                Message = "Dosya Firebase Storage'a yüklendi ve veritabanına kaydedildi.",
                Data = newNoteRecord
            });
        }

        // ==========================================
        // 3. AI İŞLEMLERİ: PDF OKUMA VE GERÇEK AI ÖZETLEME (POST: /api/notes/process-pdf)
        // ==========================================
        [HttpPost("process-pdf")]
        public async Task<IActionResult> ProcessPdf([FromBody] AiRequestDto request)
        {
            try
            {
                Console.WriteLine($"--> AI İşlem İsteği Geldi. Tip: {request.ActionType}, URL: {request.FileUrl}");

                if (string.IsNullOrEmpty(request.FileUrl))
                    return BadRequest(new { Success = false, Message = "PDF URL'si geçerli değil." });

                var pdfBytes = await _httpClient.GetByteArrayAsync(request.FileUrl);
                string extractedText = string.Empty;

                using (var document = PdfDocument.Open(pdfBytes))
                {
                    foreach (var page in document.GetPages())
                    {
                        extractedText += page.Text + "\n";
                    }
                }

                if (string.IsNullOrWhiteSpace(extractedText))
                {
                    extractedText = "Bu PDF dosyası taranmış bir görsel veya metin katmanı içermeyen bir belge.";
                }

                string resultOutput = string.Empty;

                if (request.ActionType == "summarize")
                {
                    string apiKey = Environment.GetEnvironmentVariable("OpenAIKey") ?? string.Empty;

                    if (string.IsNullOrEmpty(apiKey))
                    {
                        resultOutput = $"🤖 [HacettepeNot AI Akademik Asistanı]:\n\n" +
                                     $"Belge başarıyla parse edildi. Çıkarılan özet:\n\n" +
                                     (extractedText.Length > 600 ? extractedText.Substring(0, 600) + "...\n\n[Devamı belgede yer almaktadır]" : extractedText);
                    }
                    else
                    {
                        resultOutput = await CallOpenAiApi(apiKey, extractedText);
                    }
                }
                else
                {
                    resultOutput = extractedText;
                }

                return Ok(new { Success = true, Data = resultOutput });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"--> ❌ AI İŞLEM HATASI: {ex.Message}");
                return StatusCode(500, new { Success = false, Message = "AI İşlem hatası: " + ex.Message });
            }
        }

        // ==========================================
        // 4. BÖLÜMLER, ORTAK DERSLER VE DERSLER (GET: /api/notes/academic-structure)
        // ==========================================
        [HttpGet("academic-structure")]
        public async Task<IActionResult> GetAcademicStructure()
        {
            try
            {
                Console.WriteLine("--> Akademik yapı isteği alındı...");

                var json = await _httpClient.GetStringAsync($"{FirebaseRtdbBaseUrl}/.json");
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                {
                    return Ok(new { Success = true, Data = new AcademicStructureResponseDto() });
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var response = new AcademicStructureResponseDto();

                if (root.TryGetProperty("bolumler", out var bolumlerEl) && bolumlerEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in bolumlerEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            response.Departments.Add(item.GetString()!);
                    }
                }

                if (root.TryGetProperty("ortakdersler", out var ortakEl) && ortakEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in ortakEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            response.CommonCourses.Add(item.GetString()!);
                    }
                }

                if (root.TryGetProperty("dersler", out var derslerEl) && derslerEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var deptProp in derslerEl.EnumerateObject())
                    {
                        var deptCourses = new DepartmentCoursesDto();

                        if (deptProp.Value.ValueKind == JsonValueKind.Object)
                        {
                            if (deptProp.Value.TryGetProperty("1_sinif", out var s1El) && s1El.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in s1El.EnumerateArray())
                                {
                                    if (c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                                        deptCourses.Grade1.Add(c.GetString()!);
                                }
                            }

                            if (deptProp.Value.TryGetProperty("2_sinif", out var s2El) && s2El.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in s2El.EnumerateArray())
                                {
                                    if (c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                                        deptCourses.Grade2.Add(c.GetString()!);
                                }
                            }
                        }

                        response.CoursesByDepartment[deptProp.Name] = deptCourses;
                    }
                }

                return Ok(new { Success = true, Data = response });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"--> ❌ AKADEMİK YAPI HATASI: {ex.Message}");
                return StatusCode(500, new { Success = false, Message = "Akademik veriler alınırken hata oluştu: " + ex.Message });
            }
        }

        // ==========================================
        // 5. OKUL BİLGİLERİ: DERS PROGRAMI, YEMEK LİSTESİ, OTOBÜS SAATLERİ (GET: /api/notes/school-info)
        // ==========================================
        [HttpGet("school-info")]
        public async Task<IActionResult> GetSchoolInfo()
        {
            try
            {
                Console.WriteLine("--> Okul bilgileri isteği alındı...");

                var json = await _httpClient.GetStringAsync($"{FirebaseRtdbBaseUrl}/school.json");
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                {
                    return Ok(new { Success = true, Data = new SchoolInfoResponseDto() });
                }

                using var doc = JsonDocument.Parse(json);
                var schoolEl = doc.RootElement;

                var dto = new SchoolInfoResponseDto();

                if (schoolEl.TryGetProperty("dersprogrami", out var dpEl) && dpEl.ValueKind == JsonValueKind.String)
                {
                    dto.CourseScheduleUrl = dpEl.GetString();
                }

                if (schoolEl.TryGetProperty("yemeklistesi", out var ymEl) && ymEl.ValueKind == JsonValueKind.String)
                {
                    dto.MealListUrl = ymEl.GetString();
                }

                if (schoolEl.TryGetProperty("otobussaatleri", out var busEl) && busEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in busEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            var timeStr = item.TryGetProperty("time", out var tEl) && tEl.ValueKind == JsonValueKind.String
                                ? tEl.GetString() ?? string.Empty
                                : string.Empty;

                            string? noteStr = null;
                            if (item.TryGetProperty("note", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                            {
                                var raw = nEl.GetString()?.Trim();
                                if (!string.IsNullOrEmpty(raw)) noteStr = raw;
                            }

                            if (!string.IsNullOrWhiteSpace(timeStr))
                            {
                                dto.BusSchedule.Add(new BusScheduleItemDto
                                {
                                    Time = timeStr,
                                    Note = noteStr
                                });
                            }
                        }
                    }
                }

                return Ok(new { Success = true, Data = dto });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"--> ❌ OKUL BİLGİSİ HATASI: {ex.Message}");
                return StatusCode(500, new { Success = false, Message = "Okul bilgileri alınırken hata oluştu: " + ex.Message });
            }
        }

        // sil tekrar - OpenAI API'ye bağlanıp metni özetleten metod
        private async Task<string> CallOpenAiApi(string apiKey, string documentText)
        {
            try
            {
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                string promptText = documentText.Length > 4000 ? documentText.Substring(0, 4000) : documentText;

                var requestBody = new
                {
                    model = "gpt-3.5-turbo",
                    messages = new[]
                    {
                        new { role = "system", content = "Sen profesyonel bir üniversite akademik asistanısın. Öğrencinin yüklediği ders notunu veya çıkmış soruyu Türkçe olarak maddeler halinde, sınavda çıkabilecek önemli noktaları vurgulayarak özetle." },
                        new { role = "user", content = $"Şu ders notunu özetle:\n\n{promptText}" }
                    },
                    max_tokens = 800
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(responseString);
                    var aiMessage = doc.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString();

                    return aiMessage ?? "AI yanıt döndüremedi.";
                }
                return "⚠️ OpenAI API bağlantı hatası (Status: " + response.StatusCode + ")";
            }
            catch (Exception ex)
            {
                return "⚠️ AI Servis İstisna Hatası: " + ex.Message;
            }
        }
    }

    // DTO Sınıfları
    public class NoteUploadDto
    {
        public string CourseCode { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Uploader { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string Grade { get; set; } = string.Empty;
        public IFormFile? File { get; set; }
    }

    public class NoteRecordDto
    {
        public string Id { get; set; } = string.Empty;
        public string CourseCode { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Uploader { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string Grade { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public class AiRequestDto
    {
        public string FileUrl { get; set; } = string.Empty;
        public string ActionType { get; set; } = string.Empty; // "summarize" veya "extract-text"
    }

    // sil tekrar - Akademik yapı DTO sınıfları
    public class AcademicStructureResponseDto
    {
        [JsonPropertyName("bolumler")]
        public List<string> Departments { get; set; } = new();

        [JsonPropertyName("ortakdersler")]
        public List<string> CommonCourses { get; set; } = new();

        [JsonPropertyName("dersler")]
        public Dictionary<string, DepartmentCoursesDto> CoursesByDepartment { get; set; } = new();
    }

    public class DepartmentCoursesDto
    {
        [JsonPropertyName("1_sinif")]
        public List<string> Grade1 { get; set; } = new();

        [JsonPropertyName("2_sinif")]
        public List<string> Grade2 { get; set; } = new();
    }

    // sil tekrar - Okul bilgileri DTO sınıfları
    public class SchoolInfoResponseDto
    {
        [JsonPropertyName("dersprogrami")]
        public string? CourseScheduleUrl { get; set; }

        [JsonPropertyName("yemeklistesi")]
        public string? MealListUrl { get; set; }

        [JsonPropertyName("otobussaatleri")]
        public List<BusScheduleItemDto> BusSchedule { get; set; } = new();
    }

    public class BusScheduleItemDto
    {
        [JsonPropertyName("time")]
        public string Time { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}
