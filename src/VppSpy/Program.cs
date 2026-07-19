using VppSpy.GoodWe;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.Configure<GoodWeOptions>(builder.Configuration.GetSection(GoodWeOptions.SectionName));
builder.Services.AddSingleton<GoodWeModbusClient>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
  app.MapOpenApi();
}

app.MapGet("/api/goodwe/device-info", async (GoodWeModbusClient client, CancellationToken cancellationToken) =>
  {
    try
    {
      var info = await client.ReadDeviceInfoAsync(cancellationToken);
      return Results.Ok(info);
    }
    catch (GoodWeCommunicationException ex)
    {
      return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway,
        title: "GoodWe communication failed");
    }
  })
  .WithName("GetGoodWeDeviceInfo");

app.MapGet("/api/goodwe/discover", async (GoodWeModbusClient client, CancellationToken cancellationToken) =>
  {
    var results = await client.DiscoverAsync(cancellationToken);
    return Results.Ok(results);
  })
  .WithName("DiscoverGoodWeDevices");

var summaries = new[]
{
  "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
  {
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
          DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
          Random.Shared.Next(-20, 55),
          summaries[Random.Shared.Next(summaries.Length)]
        ))
      .ToArray();
    return forecast;
  })
  .WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
  public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}