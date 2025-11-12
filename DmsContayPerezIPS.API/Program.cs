using DmsContayPerezIPS.API.Services;               // ITextExtractor / PdfDocxTextExtractor
using DmsContayPerezIPS.Infrastructure.Persistence;
using DmsContayPerezIPS.Infrastructure.Seed;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;                    // <- necesario para IFormFile en MapType
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Minio;
using Minio.DataModel.Args;
using System.Text;

// Opcional: cargar variables desde .env si existe
try { DotNetEnv.Env.Load(); } catch { /* ignore */ }

// Opcional: compatibilidad Npgsql para DateTime
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// ===== PostgreSQL =====
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// ===== MinIO ===== (usar IMinioClient en vez de MinioClient)
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var endpoint = builder.Configuration["MinIO:Endpoint"] ?? "localhost:9000";
    var accessKey = builder.Configuration["MinIO:AccessKey"] ?? "admin";
    var secretKey = builder.Configuration["MinIO:SecretKey"] ?? "admin123";

    return new MinioClient()
        .WithEndpoint(endpoint)
        .WithCredentials(accessKey, secretKey)
        // .WithSSL() // habilítalo si usas https en MinIO
        .Build();
});

// ===== JWT Authentication =====
var jwtKey = builder.Configuration["JWT:Key"]
             ?? "EstaEsUnaClaveJWTDeAlMenos32CaracteresSuperSegura!!123";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["JWT:Issuer"],
            ValidAudience = builder.Configuration["JWT:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

// ===== Controllers + Swagger =====
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DmsContayPerezIPS.API",
        Version = "v1"
    });

    // Seguridad JWT en Swagger
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Escribe: Bearer {tu_token}"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });

    // ? Arreglo para endpoints con IFormFile en multipart/form-data
    c.MapType<IFormFile>(() => new OpenApiSchema
    {
        Type = "string",
        Format = "binary"
    });
});

// ===== Extractor de texto (PDF/DOCX) =====
builder.Services.AddScoped<ITextExtractor, PdfDocxTextExtractor>();

var app = builder.Build();

// ===== Crear bucket, ejecutar migraciones y seeding =====
using (var scope = app.Services.CreateScope())
{
    var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var bucket = builder.Configuration["MinIO:Bucket"] ?? "dms";

    try
    {
        bool exists = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket));
        if (!exists)
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));
            Console.WriteLine($"Bucket '{bucket}' creado en MinIO.");
        }
        else
        {
            Console.WriteLine($"Bucket '{bucket}' ya existe en MinIO.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error verificando/creando bucket en MinIO: {ex.Message}");
    }

    // Migraciones
    await db.Database.MigrateAsync();

    // Seeding (roles, admin, etc.)
    await SeederService.SeedAsync(db, minio, bucket);
}

// ===== Middlewares =====
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
