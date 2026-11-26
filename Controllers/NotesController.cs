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
                    .Child(uniqueFileName
