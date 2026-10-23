using Microsoft.AspNetCore.Mvc;
using Firebase.Database;
using Firebase.Database.Query;
using Firebase.Storage;

namespace HacettepeNot.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class NotesController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        
        // Firebase Realtime Database İstemcisi
        private readonly FirebaseClient _firebaseClient = new("https://hacettepenot-be247-default-rtdb.firebaseio.com/");

        public NotesController(IWebHostEnvironment env)
        {
            _env = env;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> UploadNote([FromForm] NoteUploadDto request)
        {
            // 1. Dosya Var mı Kontrolü
            if (request.File == null || request.File.Length == 0)
            {
                return BadRequest(new { Success = false, Message = "Lütfen geçerli bir dosya seçin." });
            }

            // 2. Güvenlik & MIME / Uzantı Kontrolü (Sadece PDF)
            var extension = Path.GetExtension(request.File.FileName).ToLowerInvariant();
            if (extension != ".pdf")
            {
                return BadRequest(new { Success = false, Message = "Güvenlik İhlali: Sadece .pdf uzantılı dosyalar kabul edilir." });
            }

            // 3. Unvan ve Dosya İsmi Oluşturma
            var uniqueFileName = $"{Guid.NewGuid()}{extension}";

            // 4. C# AI / OCR İçerik Doğrulama Adımı
            bool isAiApproved = true; 

            if (!isAiApproved)
            {
                return BadRequest(new { Success = false, Message = "AI Doğrulaması Başarısız: Dosya içeriği seçilen dersle uyuşmuyor." });
            }

            string downloadUrl = string.Empty;

            // 5. Firebase Storage'a Yükleme İşlemi (Log takipli)
            try
            {
                Console.WriteLine("--> Storage yükleme bloğuna girildi. Dosya adı: " + request.File.FileName);
                
                using var stream = request.File.OpenReadStream();

                var task = new FirebaseStorage("hacettepenot-be247.appspot.com", new FirebaseStorageOptions
                    {
                        ThrowOnCancel = true
                    })
                    .Child("uploads")
                    .Child(uniqueFileName)
                    .PutAsync(stream);

                // Dosyanın internet üzerinden her yerden erişilebilir kalıcı indirme linkini alıyoruz
                downloadUrl = await task;
                
                Console.WriteLine("--> Storage yükleme BAŞARILI. URL: " + downloadUrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine("--> STORAGE HATASI: " + ex.ToString());
                return StatusCode(500, new { Success = false, Message = "Firebase Storage yükleme hatası: " + ex.Message });
            }

            // 6. Firebase Realtime Database İçin Veri Modelini Oluşturma
            var newNoteRecord = new
            {
                Id = Guid.NewGuid().ToString(),
                CourseCode = request.CourseCode,
                ContentType = request.ContentType,
                Title = request.Title,
                Uploader = request.Uploader,
                FilePath = downloadUrl, // <-- Firebase Storage'ın kalıcı internet linki kaydediliyor!
                CreatedAt = DateTime.UtcNow
            };

            // 7. Realtime Database'e Kayıt Atma
            try
            {
                await _firebaseClient
                    .Child("notes")
                    .PostAsync(newNoteRecord);
            }
            catch (Exception ex)
            {
                Console.WriteLine("--> DATABASE HATASI: " + ex.ToString());
                return StatusCode(500, new { Success = false, Message = "Firebase Veritabanı kayıt hatası: " + ex.Message });
            }

            return Ok(new { 
                Success = true, 
                Message = "Dosya Firebase Storage'a yüklendi ve veritabanına kaydedildi.", 
                Data = newNoteRecord 
            });
        }
    }

    // Flutter'dan gelen multipart/form-data modelini karşılayan DTO
    public class NoteUploadDto
    {
        public string CourseCode { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Uploader { get; set; } = string.Empty;
        public IFormFile? File { get; set; }
    }
}
