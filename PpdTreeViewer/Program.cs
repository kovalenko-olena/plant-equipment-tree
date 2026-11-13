using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PpdTreeViewer;

/// <summary>
/// Усі налаштування та константи програми в одному місці.
/// Змінюйте значення тут – в решті коду нічого правити не потрібно.
/// </summary>
public static class AppSettings
{
    // ---- API ----
    public const string ApiUrl = "https://it-ppd/PPD_TOR/ws/api/_MOBILE";
    public const int HttpTimeoutSeconds = 60;

    // ---- Тіло запиту (JSON-RPC) ----
    public const string JsonRpcVersion = "2.0";
    public const string MethodName = "TestMonitoringPlan";
    public const string RequestId = "0cd24fad-4422-41ad-83d8-f87bdfbe8aaa";
    public const string AppId = "RAM";
    public const string PersonnelNumber = "000000";
    public const string FactoryCode = "00005";
    public const string DefaultDate = "2026-10-01";

    // ---- Службові поля відповіді ----
    public const string ResultProperty = "Result";
    public const string ErrorProperty = "Error";

    // ---- Колекції ієрархії ----
    public const string TechnicalPlacesCollection = "TechnicalPlaces";
    public const string EquipmentsCollection = "Equipments";
    public const string MonitoringTasksCollection = "MonitoringTasks";

    // ---- Поля, які виводяться на екран ----
    public const string TechnicalPlaceNameField = "Name";               // TechnicalPlaces.Name
    public const string EquipmentNameField = "Name";                    // Equipments.Name
    public const string MonitoringTaskNameField = "CharacteristicName"; // MonitoringTasks.CharacteristicName

    // ---- Виведення ----
    public const string IndentUnit = "    ";            // відступ на один рівень ієрархії
    public const string UnnamedNodeText = "(без назви)";
    public const string ChildrenCountFormat = " ({0})"; // як виводиться кількість дітей
}

/// <summary>
/// Точка входу: лише збирає залежності та запускає сценарій.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // Дату можна передати першим аргументом: dotnet run -- 2026-10-01
        var date = args.Length > 0 ? args[0] : AppSettings.DefaultDate;

        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(AppSettings.HttpTimeoutSeconds)
            };

            var apiClient = new MonitoringPlanApiClient(httpClient);
            var treeBuilder = new JsonTreeBuilder();
            var treePrinter = new TreePrinter(Console.Out);

            var json = await apiClient.GetMonitoringPlanJsonAsync(date);
            var tree = treeBuilder.Build(json);

            treePrinter.Print(tree);
            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Помилка звернення до API: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Помилка: {ex.Message}");
            return 1;
        }
    }
}

/// <summary>
/// Вузол дерева: назва та дочірні елементи. Не знає ні про JSON, ні про виведення.
/// </summary>
public sealed class TreeNode
{
    public TreeNode(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public List<TreeNode> Children { get; } = new();

    /// <summary>Кількість прямих дочірніх елементів.</summary>
    public int ChildrenCount => Children.Count;
}

/// <summary>
/// Відповідає лише за виклик API та отримання сирої JSON-відповіді.
/// </summary>
public sealed class MonitoringPlanApiClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient;

    public MonitoringPlanApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> GetMonitoringPlanJsonAsync(string date, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            Jsonrpc = AppSettings.JsonRpcVersion,
            Method = AppSettings.MethodName,
            Id = AppSettings.RequestId,
            Params = new
            {
                AppId = AppSettings.AppId,
                PersonnelNumber = AppSettings.PersonnelNumber,
                FactoryCode = AppSettings.FactoryCode,
                Date = date
            }
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(AppSettings.ApiUrl, content, cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

/// <summary>
/// Відповідає лише за перетворення JSON-відповіді API у дерево TreeNode.
/// Вкладеність шукається рекурсивно, тому підтримується будь-яка глибина.
/// </summary>
public sealed class JsonTreeBuilder
{
    private static readonly (string Collection, string NameField)[] Levels =
    {
        (AppSettings.TechnicalPlacesCollection, AppSettings.TechnicalPlaceNameField),
        (AppSettings.EquipmentsCollection, AppSettings.EquipmentNameField),
        (AppSettings.MonitoringTasksCollection, AppSettings.MonitoringTaskNameField)
    };

    public IReadOnlyList<TreeNode> Build(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (TryGetProperty(root, AppSettings.ErrorProperty, out var error) && error.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException($"API повернуло помилку: {error}");
        }

        if (TryGetProperty(root, AppSettings.ResultProperty, out var result))
        {
            root = result;
        }

        // Корінь може бути масивом технічних місць або об'єктом, що їх містить.
        if (root.ValueKind == JsonValueKind.Array)
        {
            return BuildNodes(root, AppSettings.TechnicalPlaceNameField);
        }

        return BuildChildren(root);
    }

    private List<TreeNode> BuildChildren(JsonElement element)
    {
        var nodes = new List<TreeNode>();

        if (element.ValueKind != JsonValueKind.Object)
        {
            return nodes;
        }

        foreach (var (collection, nameField) in Levels)
        {
            if (TryGetProperty(element, collection, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                nodes.AddRange(BuildNodes(array, nameField));
            }
        }

        return nodes;
    }

    private List<TreeNode> BuildNodes(JsonElement array, string nameField)
    {
        var nodes = new List<TreeNode>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var node = new TreeNode(ReadName(item, nameField));
            node.Children.AddRange(BuildChildren(item));
            nodes.Add(node);
        }

        return nodes;
    }

    private static string ReadName(JsonElement item, string nameField)
    {
        if (TryGetProperty(item, nameField, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return AppSettings.UnnamedNodeText;
    }

    /// <summary>Пошук властивості без урахування регістру.</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

/// <summary>
/// Відповідає лише за виведення дерева: відступи за рівнем та кількість дочірніх елементів у назві.
/// </summary>
public sealed class TreePrinter
{
    private readonly TextWriter _writer;

    public TreePrinter(TextWriter writer)
    {
        _writer = writer;
    }

    public void Print(IEnumerable<TreeNode> roots)
    {
        foreach (var root in roots)
        {
            PrintNode(root, 0);
        }
    }

    private void PrintNode(TreeNode node, int level)
    {
        var indent = string.Concat(Enumerable.Repeat(AppSettings.IndentUnit, level));
        var suffix = node.ChildrenCount > 0
            ? string.Format(AppSettings.ChildrenCountFormat, node.ChildrenCount)
            : string.Empty;

        _writer.WriteLine($"{indent}{node.Name}{suffix}");

        foreach (var child in node.Children)
        {
            PrintNode(child, level + 1);
        }
    }
}
