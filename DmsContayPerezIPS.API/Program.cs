using DmsContayPerezIPS.API.Services;               // ITextExtractor / PdfDocxTextExtractor
using DmsContayPerezIPS.Infrastructure.Persistence;
using DmsContayPerezIPS.Infrastructure.Seed;        // 👈 SeederService
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;                    // Para IFormFile en Swagger MapType
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Minio;
using Minio.DataModel.Args;
using System.IdentityModel.Tokens.Jwt;              // 👈 RoleClaimType
using System.Security.Claims;                       // 👈 RoleClaimType
using System.Text;

// ====== Opcional: cargar variables desde .env si existe ======
try { DotNetEnv.Env.Load(); } catch { /* ignore */ }

// ====== Opcional: compatibilidad Npgsql para DateTime ======
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// ===== PostgreSQL =====
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var cs = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseNpgsql(cs);
});

// ===== Servicios de aplicación (ej. extractor de texto) =====
builder.Services.AddSingleton<ITextExtractor, PdfDocxTextExtractor>();

// ===== MinIO =====
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

// ===== Controllers =====
builder.Services.AddControllers();

// ===== Swagger =====
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Soporte para IFormFile
    c.MapType<IFormFile>(() => new OpenApiSchema
    {
        Type = "string",
        Format = "binary"
    });

    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DmsContayPerezIPS API",
        Version = "v1"
    });

    // Autenticación Bearer en Swagger
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Bearer. Ej: 'Bearer {token}'",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme {
                Reference = new OpenApiReference {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ===== JWT Authentication =====
var jwtKey = builder.Configuration["JWT:Key"]
             ?? "EstaEsUnaClaveJWTDeAlMenos32CaracteresSuperSegura!!123";

// 👇 Evitar remapeo de claims para que ClaimTypes.Role llegue intacto
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),

            // 👇 Claves para que [Authorize(Roles="...")] funcione
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = ClaimTypes.Name
        };
    });

// ===== Authorization (políticas por serie; Admin entra en todas) =====
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Serie_GestClinica", p => p.RequireRole("Admin", "GestClinica"));
    options.AddPolicy("Serie_GestiAdmin", p => p.RequireRole("Admin", "GestiAdmin"));
    options.AddPolicy("Serie_GestFinYCon", p => p.RequireRole("Admin", "GestFinYCon"));
    options.AddPolicy("Serie_GestJurid", p => p.RequireRole("Admin", "GestJurid"));
    options.AddPolicy("Serie_GestCalidad", p => p.RequireRole("Admin", "GestCalidad"));
    options.AddPolicy("Serie_SGSST", p => p.RequireRole("Admin", "SGSST"));
    options.AddPolicy("Serie_AdminEquBiomed", p => p.RequireRole("Admin", "AdminEquBiomed"));
});

var app = builder.Build();

// ===== Crear bucket, ejecutar migraciones y seeding =====
using (var scope = app.Services.CreateScope())
{
    var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var bucket = builder.Configuration["MinIO:Bucket"] ?? "dms";

    try
    {
        // 1) Asegurar bucket (si falla no bloquea el arranque)
        bool exists = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket));
        if (!exists)
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[MinIO] Warn: {ex.Message}");
    }

    try
    {
        // 2) Migraciones
        await db.Database.MigrateAsync();

        // 3) Seeder real de tu repo (👈 este es el correcto)
        await SeederService.SeedAsync(db, minio, bucket);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[EF/Seed] Warn: {ex.Message}");
    }
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
