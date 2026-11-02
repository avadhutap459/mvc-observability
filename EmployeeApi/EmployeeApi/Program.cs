using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using EmployeeApi.Data;
using EmployeeApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString =
        "InstrumentationKey=b323d4f6-0dc1-4a35-9dd3-242d0dde977a;IngestionEndpoint=https://southindia-0.in.applicationinsights.azure.com/;LiveEndpoint=https://southindia.livediagnostics.monitor.azure.com/;ApplicationId=f2d90599-abcd-4a19-b4be-5269d3fb7fe9";
});

// AWS credentials
var accessKey = builder.Configuration["AWS:AccessKey"];
var secretKey = builder.Configuration["AWS:SecretKey"];
var region = builder.Configuration["AWS:Region"];

if (string.IsNullOrWhiteSpace(accessKey) ||
    string.IsNullOrWhiteSpace(secretKey) ||
    string.IsNullOrWhiteSpace(region))
{
    throw new InvalidOperationException(
        "AWS configuration is missing.");
}

var credentials = new BasicAWSCredentials(
    accessKey,
    secretKey);

var regionEndpoint =
    RegionEndpoint.GetBySystemName(region);

builder.Services.AddSingleton<IAmazonS3>(
    new AmazonS3Client(credentials, regionEndpoint));

builder.Services.AddScoped<S3SessionRecordingService>();

// Custom telemetry service
builder.Services.AddSingleton<TelemetryService>();
// Add services to the container.

builder.Services.AddControllers();


builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSessionCapture", policy =>
    {
        policy
            .WithOrigins("https://localhost:7131")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.AddScoped<FfmpegVideoConverter>();
builder.Services.AddScoped<SessionReplayOrchestrator>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));

//builder.Services.AddCors(options =>
//{
//    options.AddPolicy("AngularClient", policy =>
//    {
//        policy.WithOrigins("http://localhost:4200")
//              .AllowAnyHeader()
//              .AllowAnyMethod();
//    });
//});



// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowSessionCapture");
//app.UseCors("AngularClient");

app.UseAuthorization();

app.MapControllers();

app.Run();
