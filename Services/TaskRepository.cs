using System.Collections;
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

        private readonly ArrayList tasks = new ArrayList();
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
        public Object?[] All()
        {
            return tasks.ToArray();
        }

        /// <summary>إضافة مهمة جديدة وإرجاعها بعد الحفظ.</summary>
        public TodoItem Add(String title, String notes)
        {
            TodoItem item = new TodoItem(nextId++, title, notes, false, DateTime.Now.ToString());
            tasks.Add(item);
            Persist();
            return item;
        }

        /// <summary>تبديل حالة إنجاز مهمة.</summary>
        public bool Toggle(long id)
        {
            ArrayList rebuilt = new ArrayList();
            bool found = false;
            Object?[] arr = tasks.ToArray();
            for (int i = 0; i < arr.Length; i++)
            {
                TodoItem item = (TodoItem) arr[i];
                if (!found && item.id == id)
                {
                    item = new TodoItem(item.id, item.title, item.notes, !item.done, item.createdAt);
                    found = true;
                }
                rebuilt.Add(item);
            }
            if (found)
            {
                ReplaceAll(rebuilt);
                Persist();
            }
            return found;
        }

        /// <summary>حذف مهمة.</summary>
        public bool Delete(long id)
        {
            ArrayList rebuilt = new ArrayList();
            bool found = false;
            Object?[] arr = tasks.ToArray();
            for (int i = 0; i < arr.Length; i++)
            {
                TodoItem item = (TodoItem) arr[i];
                if (!found && item.id == id)
                {
                    found = true;
                }
                else
                {
                    rebuilt.Add(item);
                }
            }
            if (found)
            {
                ReplaceAll(rebuilt);
                Persist();
            }
            return found;
        }

        private void ReplaceAll(ArrayList source)
        {
            tasks.Clear();
            Object?[] arr = source.ToArray();
            for (int i = 0; i < arr.Length; i++)
            {
                tasks.Add(arr[i]);
            }
        }

        private void Persist()
        {
            try
            {
                File.WriteAllText("data/tasks.json", ToJson());
            }
            catch (Exception ex)
            {
                // تجاهل أخطاء الحفظ في وضع العرض البسيط
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists("data/tasks.json"))
                {
                    return;
                }
                LoadTasks(File.ReadAllText("data/tasks.json"));
                nextId = 1;
                Object?[] arr = tasks.ToArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    TodoItem item = (TodoItem) arr[i];
                    if (item.id >= nextId)
                    {
                        nextId = item.id + 1;
                    }
                }
            }
            catch (Exception ex)
            {
                tasks.Clear();
            }
        }

        private void LoadTasks(String text)
        {
            int arrayStart = text.IndexOf('[');
            if (arrayStart < 0)
            {
                return;
            }
            tasks.Clear();
            int i = arrayStart + 1;
            while (i < text.Length)
            {
                int objStart = text.IndexOf('{', i);
                if (objStart < 0)
                {
                    return;
                }
                int objEnd = FindObjectEnd(text, objStart);
                if (objEnd < 0)
                {
                    return;
                }
                tasks.Add(ParseTaskObject(text.Substring(objStart, objEnd - objStart + 1)));
                i = objEnd + 1;
            }
        }

        private static int FindObjectEnd(String text, int start)
        {
            bool inString = false;
            int depth = 0;
            int i = start;
            while (i < text.Length)
            {
                String c = text.Substring(i, 1);
                if (inString)
                {
                    if (c.StartsWith("\\"))
                    {
                        i = i + 1;
                    }
                    else if (c.StartsWith("\""))
                    {
                        inString = false;
                    }
                }
                else if (c.StartsWith("\""))
                {
                    inString = true;
                }
                else if (c.StartsWith("{"))
                {
                    depth++;
                }
                else if (c.StartsWith("}"))
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
                i++;
            }
            return -1;
        }

        private static TodoItem ParseTaskObject(String obj)
        {
            return new TodoItem(
                ReadLong(obj, "\"id\""),
                ReadString(obj, "\"title\""),
                ReadString(obj, "\"notes\""),
                ReadBool(obj, "\"done\""),
                ReadString(obj, "\"createdAt\""));
        }

        private static String ReadString(String obj, String key)
        {
            int k = obj.IndexOf(key);
            if (k < 0)
            {
                return "";
            }
            int start = obj.IndexOf('"', k + key.Length);
            if (start < 0)
            {
                return "";
            }
            start++;
            String result = "";
            int i = start;
            while (i < obj.Length)
            {
                String c = obj.Substring(i, 1);
                if (c.StartsWith("\""))
                {
                    break;
                }
                if (c.StartsWith("\\") && i + 1 < obj.Length)
                {
                    String e = obj.Substring(i + 1, 1);
                    if (e.StartsWith("n")) { result = result + "\n"; }
                    else if (e.StartsWith("t")) { result = result + "\t"; }
                    else if (e.StartsWith("r")) { result = result + "\r"; }
                    else if (e.StartsWith("\"")) { result = result + "\""; }
                    else if (e.StartsWith("\\")) { result = result + "\\"; }
                    else if (e.StartsWith("/")) { result = result + "/"; }
                    else if (e.StartsWith("u") && i + 5 < obj.Length)
                    {
                        result = result + (char) HexValue(obj.Substring(i + 2, 4));
                        i = i + 5;
                    }
                    else
                    {
                        result = result + e;
                    }
                    i++;
                }
                else
                {
                    result = result + c;
                }
                i++;
            }
            return result;
        }

        private static long ReadLong(String obj, String key)
        {
            long value = 0;
            int k = obj.IndexOf(key);
            if (k < 0)
            {
                return 0L;
            }
            int start = k + key.Length;
            while (start < obj.Length && !obj.Substring(start, 1).StartsWith(":"))
            {
                start++;
            }
            start++;
            while (start < obj.Length && obj.Substring(start, 1).StartsWith(" "))
            {
                start++;
            }
            bool neg = start < obj.Length && obj.Substring(start, 1).StartsWith("-");
            if (neg)
            {
                start++;
            }
            bool any = false;
            for (int i = start; i < obj.Length; i++)
            {
                String c = obj.Substring(i, 1);
                if (!IsDigit(c))
                {
                    break;
                }
                value = value * 10 + DigitValue(c);
                any = true;
            }
            if (!any)
            {
                return 0L;
            }
            return neg ? 0L - value : value;
        }

        private static bool IsDigit(String c)
        {
            return c.StartsWith("0") || c.StartsWith("1") || c.StartsWith("2") || c.StartsWith("3")
                || c.StartsWith("4") || c.StartsWith("5") || c.StartsWith("6") || c.StartsWith("7")
                || c.StartsWith("8") || c.StartsWith("9");
        }

        private static int DigitValue(String c)
        {
            if (c.StartsWith("0")) { return 0; }
            if (c.StartsWith("1")) { return 1; }
            if (c.StartsWith("2")) { return 2; }
            if (c.StartsWith("3")) { return 3; }
            if (c.StartsWith("4")) { return 4; }
            if (c.StartsWith("5")) { return 5; }
            if (c.StartsWith("6")) { return 6; }
            if (c.StartsWith("7")) { return 7; }
            if (c.StartsWith("8")) { return 8; }
            return 9;
        }

        private static bool ReadBool(String obj, String key)
        {
            int k = obj.IndexOf(key);
            if (k < 0)
            {
                return false;
            }
            int start = k + key.Length;
            while (start < obj.Length && !obj.Substring(start, 1).StartsWith(":"))
            {
                start++;
            }
            start++;
            while (start < obj.Length && obj.Substring(start, 1).StartsWith(" "))
            {
                start++;
            }
            return start < obj.Length && obj.Substring(start, 1).StartsWith("t");
        }

        private static int HexValue(String hex)
        {
            int value = 0;
            for (int i = 0; i < hex.Length; i++)
            {
                String c = hex.Substring(i, 1);
                value = value * 16;
                if (c.StartsWith("0")) { value = value + 0; }
                else if (c.StartsWith("1")) { value = value + 1; }
                else if (c.StartsWith("2")) { value = value + 2; }
                else if (c.StartsWith("3")) { value = value + 3; }
                else if (c.StartsWith("4")) { value = value + 4; }
                else if (c.StartsWith("5")) { value = value + 5; }
                else if (c.StartsWith("6")) { value = value + 6; }
                else if (c.StartsWith("7")) { value = value + 7; }
                else if (c.StartsWith("8")) { value = value + 8; }
                else if (c.StartsWith("9")) { value = value + 9; }
                else if (c.StartsWith("a")) { value = value + 10; }
                else if (c.StartsWith("b")) { value = value + 11; }
                else if (c.StartsWith("c")) { value = value + 12; }
                else if (c.StartsWith("d")) { value = value + 13; }
                else if (c.StartsWith("e")) { value = value + 14; }
                else if (c.StartsWith("f")) { value = value + 15; }
                else if (c.StartsWith("A")) { value = value + 10; }
                else if (c.StartsWith("B")) { value = value + 11; }
                else if (c.StartsWith("C")) { value = value + 12; }
                else if (c.StartsWith("D")) { value = value + 13; }
                else if (c.StartsWith("E")) { value = value + 14; }
                else if (c.StartsWith("F")) { value = value + 15; }
            }
            return value;
        }

        private String ToJson()
        {
            String json = "{\n  \"tasks\": [\n";
            Object?[] arr = tasks.ToArray();
            for (int i = 0; i < arr.Length; i++)
            {
                TodoItem t = (TodoItem) arr[i];
                if (i > 0)
                {
                    json = json + ",\n";
                }
                json = json + "    { \"id\": " + t.id
                     + ", \"title\": " + Quote(t.title)
                     + ", \"notes\": " + Quote(t.notes)
                     + ", \"done\": " + t.done
                     + ", \"createdAt\": " + Quote(t.createdAt) + " }";
            }
            json = json + "\n  ]\n}\n";
            return json;
        }

        private static String Quote(String value)
        {
            String result = "\"";
            for (int i = 0; i < value.Length; i++)
            {
                String c = value.Substring(i, 1);
                if (c.StartsWith("\"")) { result = result + "\\\""; }
                else if (c.StartsWith("\\")) { result = result + "\\\\"; }
                else if (c.StartsWith("\n")) { result = result + "\\n"; }
                else if (c.StartsWith("\r")) { result = result + "\\r"; }
                else if (c.StartsWith("\t")) { result = result + "\\t"; }
                else { result = result + c; }
            }
            result = result + "\"";
            return result;
        }
    }
}