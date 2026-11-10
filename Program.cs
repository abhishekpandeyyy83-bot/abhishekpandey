using System.Net.Mail;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var products = new Dictionary<string, Product>
{
    ["sunday-tote"] = new("sunday-tote", "The Sunday Tote", "Organic cotton · Natural", 4800, "https://images.unsplash.com/photo-1590874103328-eac38a683ce7?auto=format&fit=crop&w=900&q=80", "Natural canvas everyday tote bag", "Bestseller"),
    ["cloud-knit-throw"] = new("cloud-knit-throw", "Cloud Knit Throw", "Recycled cotton · Oat", 8600, "https://images.unsplash.com/photo-1600210492486-724fe5c67fb0?auto=format&fit=crop&w=900&q=80", "Soft neutral knit throw in a calm living room", "New"),
    ["sculpted-stoneware"] = new("sculpted-stoneware", "Sculpted Stoneware", "Hand-finished · Chalk", 5400, "https://images.unsplash.com/photo-1578749556568-bc2c40e68b61?auto=format&fit=crop&w=900&q=80", "Handmade sculptural ceramic stoneware vase", null),
    ["slow-morning-candle"] = new("slow-morning-candle", "Slow Morning Candle", "Soy wax · Cedar & fig", 3200, "https://images.unsplash.com/photo-1603006905003-be475563bc59?auto=format&fit=crop&w=900&q=80", "Minimal soy candle for a slow morning", "Almost gone")
};

var dataFile = Environment.GetEnvironmentVariable("MORROW_DATA_FILE");
if (string.IsNullOrWhiteSpace(dataFile))
{
    dataFile = Path.Combine(app.Environment.ContentRootPath, "data", "shop.json");
}

var store = new ShopStore(dataFile);

app.Use(async (context, next) =>
{
    if (context.Request.ContentLength is > 16_384)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new { error = "Request body is too large." });
        return;
    }

    await next();
});

app.MapGet("/", (IWebHostEnvironment environment) =>
    Results.File(Path.Combine(environment.ContentRootPath, "index.html"), "text/html; charset=utf-8"));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/products", () => Results.Ok(products.Values));

app.MapPost("/api/newsletter", (NewsletterRequest? request) =>
{
    if (request is null || !IsValidEmail(request.Email))
    {
        return Results.BadRequest(new { error = "Enter a valid email address." });
    }

    store.Subscribe(request.Email!.Trim());
    return Results.Ok(new { subscribed = true });
});

app.MapPost("/api/orders", (OrderRequest? request) =>
{
    if (request is null || !IsValidEmail(request.Email))
    {
        return Results.BadRequest(new { error = "Enter a valid email address." });
    }

    if (request.Items is null || request.Items.Count is < 1 or > 20)
    {
        return Results.BadRequest(new { error = "An order must contain between 1 and 20 different products." });
    }

    var lines = new List<OrderLine>();
    var seenProductIds = new HashSet<string>(StringComparer.Ordinal);
    var totalQuantity = 0;

    foreach (var item in request.Items)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.ProductId) ||
            item.Quantity is < 1 or > 10 || !seenProductIds.Add(item.ProductId))
        {
            return Results.BadRequest(new { error = "Each product must be unique and have a quantity between 1 and 10." });
        }

        if (!products.TryGetValue(item.ProductId, out var product))
        {
            return Results.BadRequest(new { error = "The order contains an unavailable product." });
        }

        totalQuantity += item.Quantity;
        if (totalQuantity > 50)
        {
            return Results.BadRequest(new { error = "An order cannot contain more than 50 items." });
        }

        lines.Add(new OrderLine(product.Id, product.Name, product.PriceCents, item.Quantity));
    }

    var order = store.CreateOrder(request.Email!.Trim(), lines);
    return Results.Ok(new
    {
        order.Id,
        order.CreatedAt,
        order.TotalCents,
        order.Items
    });
});

app.Run();

static bool IsValidEmail(string? email)
{
    if (string.IsNullOrWhiteSpace(email) || email.Length > 254)
    {
        return false;
    }

    try
    {
        return new MailAddress(email.Trim()).Address.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase);
    }
    catch (FormatException)
    {
        return false;
    }
}

record Product(string Id, string Name, string Detail, int PriceCents, string Image, string ImageAlt, string? Tag);
record NewsletterRequest(string? Email);
record OrderRequest(string? Email, List<OrderItemRequest?>? Items);
record OrderItemRequest(string? ProductId, int Quantity);
record OrderLine(string ProductId, string Name, int UnitPriceCents, int Quantity);
record ShopOrder(string Id, string Email, DateTimeOffset CreatedAt, int TotalCents, List<OrderLine> Items);
record ShopData(List<string> Subscribers, List<ShopOrder> Orders);

sealed class ShopStore
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private ShopData _data;

    public ShopStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        _data = Load();
    }

    public void Subscribe(string email)
    {
        lock (_gate)
        {
            if (_data.Subscribers.Contains(email, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _data = _data with { Subscribers = [.. _data.Subscribers, email] };
            Save();
        }
    }

    public ShopOrder CreateOrder(string email, List<OrderLine> items)
    {
        lock (_gate)
        {
            var totalCents = items.Sum(item => checked(item.UnitPriceCents * item.Quantity));
            var order = new ShopOrder(
                Guid.NewGuid().ToString("N"),
                email,
                DateTimeOffset.UtcNow,
                totalCents,
                items);

            _data = _data with { Orders = [.. _data.Orders, order] };
            Save();
            return order;
        }
    }

    private ShopData Load()
    {
        if (!File.Exists(_filePath))
        {
            return new ShopData([], []);
        }

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<ShopData>(json, _jsonOptions)
            ?? throw new InvalidDataException($"The shop data file '{_filePath}' is empty or invalid.");
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The shop data file must have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_data, _jsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
