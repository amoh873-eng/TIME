using System.Text.Json;
using time.Models;

namespace time.Services
{
    /// <summary>
    /// مخزن المهام: يحفظ البيانات في ملف JSON محلي (data/tasks.json)
    /// ويُعيد تحميلها تلقائياً عند تشغيل التطبيق.
    /// </summary>
    public class TaskRepository
    {
        private static readonly TaskRepository sharedInstance = new TaskRepository();

        private readonly List<TodoItem> tasks = new List<TodoItem>();
        private long nextId = 1;

        private TaskRepository()
        {
            Load();
        }

        /// <summary>نقطة الوصول الوحيدة للمخزن (Singleton).</summary>
        public static TaskRepository shared()
        {
            return sharedInstance;
        }

        /// <summary>إرجاع مصفوفة بكل المهام.</summary>
        public TodoItem[] All()
        {
            return tasks.ToArray();
        }

        /// <summary>إضافة مهمة جديدة وإرجاعها بعد الحفظ.</summary>
        public TodoItem Add(string title, string notes)
        {
            TodoItem item = new TodoItem(nextId++, title, notes, false, DateTime.Now.ToString());
            tasks.Add(item);
            Persist();
            return item;
        }

        /// <summary>تبديل حالة إنجاز مهمة.</summary>
        public bool Toggle(long id)
        {
            int index = tasks.FindIndex(t => t.id == id);
            if (index < 0)
            {
                return false;
            }

            TodoItem item = tasks[index];
            tasks[index] = item with { done = !item.done };
            Persist();
            return true;
        }

        /// <summary>حذف مهمة.</summary>
        public bool Delete(long id)
        {
            int index = tasks.FindIndex(t => t.id == id);
            if (index < 0)
            {
                return false;
            }

            tasks.RemoveAt(index);
            Persist();
            return true;
        }

        private void Persist()
        {
            try
            {
                Directory.CreateDirectory("data");
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(new { tasks }, options);
                File.WriteAllText(Path.Combine("data", "tasks.json"), json);
            }
            catch
            {
                // تجاهل أخطاء الحفظ في وضع العرض البسيط.
            }
        }

        private void Load()
        {
            try
            {
                string path = Path.Combine("data", "tasks.json");
                if (!File.Exists(path))
                {
                    return;
                }

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("tasks", out JsonElement arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    tasks.Clear();
                    foreach (JsonElement item in arr.EnumerateArray())
                    {
                        tasks.Add(new TodoItem(
                            ReadLong(item, "id"),
                            ReadString(item, "title"),
                            ReadString(item, "notes"),
                            item.TryGetProperty("done", out JsonElement done) && done.ValueKind == JsonValueKind.True,
                            ReadString(item, "createdAt")));
                    }
                }

                nextId = 1;
                foreach (TodoItem item in tasks)
                {
                    if (item.id >= nextId)
                    {
                        nextId = item.id + 1;
                    }
                }
            }
            catch
            {
                tasks.Clear();
            }
        }

        private static long ReadLong(JsonElement item, string key)
        {
            return item.TryGetProperty(key, out JsonElement value) ? value.GetInt64() : 0L;
        }

        private static string ReadString(JsonElement item, string key)
        {
            return item.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }
    }
}