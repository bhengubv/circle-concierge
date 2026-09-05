using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Shared.Skills;

public static class SkillSourceServiceCollectionExtensions
{
    /// <summary>
    /// Walks the canonical local skill library at
    /// <c>C:\Dev\Solutions\com.bhengubv\Skills\</c> and registers any
    /// existing subfolders as <see cref="FileSystemSkillSource"/>s. No-op on
    /// runtimes where the folder is absent (mobile, CI, prod boxes) — those
    /// runtimes get only the embedded bundle.
    ///
    /// Subfolders we wire up:
    ///   • "01 - Concierge Curated Skills" — owner-curated additions
    ///   • "02 - Downloaded Skill Sources" — third-party libraries
    ///     (Claude-BugHunter, loki-mode, anthropic-skills, vercel-agent-skills,
    ///     etc.)
    ///   • "03 - Skill Inventory and Review" — staging area
    ///
    /// You can also override the root via the <c>CONCIERGE_SKILLS_ROOT</c>
    /// environment variable for portable installs.
    /// </summary>
    public static IServiceCollection AddLocalSkillSources(this IServiceCollection services)
    {
        // Folders you have added yourself, from the Skills panel. Registered
        // first and unconditionally: unlike the paths below it does not depend
        // on an environment variable or on a folder existing on the machine
        // this was written on.
        services.AddSingleton<UserSkillFolders>(_ => new UserSkillFolders());
        services.AddSingleton<ISkillSource>(sp =>
            new UserFolderSkillSource(sp.GetRequiredService<UserSkillFolders>()));

        var root = Environment.GetEnvironmentVariable("CONCIERGE_SKILLS_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            // Canonical dev path. Skipped silently when the folder doesn't
            // exist — production / mobile won't have it.
            root = @"C:\Dev\Solutions\com.bhengubv\Skills";
        }

        if (!Directory.Exists(root)) return services;

        // Register one source per subfolder so the UI can show provenance.
        var subfolders = new[]
        {
            "01 - Concierge Curated Skills",
            "02 - Downloaded Skill Sources",
            "03 - Skill Inventory and Review",
        };
        foreach (var name in subfolders)
        {
            var path = Path.Combine(root, name);
            if (Directory.Exists(path))
            {
                var safeName = name.Split(' ', 2).Last();   // strip leading "01 - " etc.
                services.AddSingleton<ISkillSource>(new FileSystemSkillSource(safeName, path));
            }
        }
        return services;
    }

    /// <summary>Register a <see cref="FileSystemSkillSource"/> for an explicit
    /// set of folder roots. Use when you want the skills folder somewhere
    /// other than the canonical <c>C:\Dev\Solutions\com.bhengubv\Skills\</c>.</summary>
    public static IServiceCollection AddSkillFolder(this IServiceCollection services, string name, params string[] roots)
    {
        services.AddSingleton<ISkillSource>(new FileSystemSkillSource(name, roots));
        return services;
    }
}
