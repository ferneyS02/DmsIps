using System.Text;
using BCrypt.Net;
using DmsContayPerezIPS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Minio;
using Minio.DataModel.Args;

var builder = WebApplication.CreateBuilder(args);

// =========================================
// PostgreSQL
// =========================================
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// =========================================
/* MinIO - usa IMinioClient */
// =========================================
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var cfg = builder.Configuration;
    var endpoint = cfg["MinIO:Endpoint"] ?? "localhost:9000";
    var accessKey = cfg["MinIO:AccessKey"] ?? "admin";
    var secretKey = cfg["MinIO:SecretKey"] ?? "admin123";
    var useSSL = bool.TryParse(cfg["MinIO:UseSSL"], out var ssl) && ssl;

    var client = new MinioClient()
        .WithEndpoint(endpoint)
        .WithCredentials(accessKey, secretKey);

    if (useSSL) client = client.WithSSL();

    return client.Build();
});

// =========================================
// Controllers + JSON
// =========================================
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // Mantén camelCase (por defecto). Agrega aquí si necesitas algo especial.
        // o.JsonSerializerOptions.PropertyNamingPolicy = null;
    });

// =========================================
// CORS (ajusta orígenes del front Angular)
// =========================================
builder.Services.AddCors(options =>
{
    var origins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:4200")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    options.AddPolicy("frontend", policy =>
    {
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// =========================================
// Límites para multipart/form-data (uploads grandes)
// =========================================
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1L * 1024 * 1024 * 1024; // 1 GB
    o.ValueCountLimit = int.MaxValue;
    o.ValueLengthLimit = int.MaxValue;
    o.MemoryBufferThreshold = int.MaxValue;
});

// =========================================
// JWT Auth
// =========================================
var jwtKey = builder.Configuration["JWT:Key"]
            ?? "EstaEsUnaClaveJWTDeAlMenos32CaracteresSuperSegura!!123";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false; // ponlo en true en prod con HTTPS
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["JWT:Issuer"],
            ValidAudience = builder.Configuration["JWT:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),

            // 👇 Muy importante para que [Authorize(Roles="...")] y User.Identity.Name funcionen
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
    });

// =========================================
// Authorization (política para forzar cambio de contraseña)
// =========================================
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PwdFresh", policy =>
        policy.RequireClaim("pwd_fresh", "true"));
});

// =========================================
// Swagger
// =========================================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DmsContayPerezIPS API",
        Version = "v1"
    });

    // Bearer Auth
    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "JWT Bearer. Ej: Bearer {token}",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new OpenApiReference
        {
            Type = ReferenceType.SecurityScheme,
            Id = "Bearer"
        }
    };

    c.AddSecurityDefinition("Bearer", securityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { securityScheme, Array.Empty<string>() }
    });
});

// =========================================
// Kestrel (opcional: subir límite general del body)
// =========================================
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 1L * 1024 * 1024 * 1024; // 1 GB
});

var app = builder.Build();

// =========================================
// Dev tools
// =========================================
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // En prod también puedes habilitar Swagger si deseas:
    app.UseSwagger();
    app.UseSwaggerUI();
}

// =========================================
// CORS / Auth
// =========================================
app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// =========================================
// Inicialización: migraciones + bucket + admin opcional
// =========================================
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;

    // ---- Aplicar migraciones (si procede) ----
    try
    {
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }
    catch (Exception)
    {
        // Si no quieres auto-migrar, comenta el bloque de migrate.
        // Loguea si deseas.
    }

    // ---- Asegurar bucket MinIO ----
    try
    {
        var cfg = builder.Configuration;
        var minio = sp.GetRequiredService<IMinioClient>();
        var bucket = cfg["MinIO:Bucket"] ?? "dms";

        bool exists = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket));
        if (!exists)
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));
    }
    catch (Exception)
    {
        // Loguea si deseas.
    }

    // ---- Seed opcional de ADMIN desde appsettings ----
    // Coloca en appsettings.Development.json (o variables de entorno):
    // "Admin:Username": "admin",
    // "Admin:Password": "Admin123*"
    try
    {
        var cfg = builder.Configuration;
        var adminUser = cfg["Admin:Username"];
        var adminPass = cfg["Admin:Password"];
        if (!string.IsNullOrWhiteSpace(adminUser) && !string.IsNullOrWhiteSpace(adminPass))
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var adminRoleId = await db.Roles
                .Where(r => r.Name == "Admin")
                .Select(r => r.Id)
                .FirstOrDefaultAsync();

            if (adminRoleId != 0 && !await db.Users.AnyAsync(u => u.Username == adminUser))
            {
                db.Users.Add(new DmsContayPerezIPS.Domain.Entities.User
                {
                    Username = adminUser,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPass),
                    RoleId = adminRoleId,
                    MustChangePassword = true,    // obliga a cambiarla al primer login
                    PasswordChangedAt = null,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
        }
    }
    catch (Exception)
    {
        // Loguea si deseas.
    }
}

app.Run();
