using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Library;

namespace GameInterface.Services.Modules
{
    public struct ModuleInfo
    {
        public ModuleInfo(
            string id,
            bool isOfficial,
            bool isDlc,
            ApplicationVersion version) : this()
        {
            Id = id;
            IsOfficial = isOfficial;
            IsDlc = isDlc;
            Version = version;
        }

        public string Id { get; set; }
        public bool IsOfficial { get; set; }
        public bool IsDlc { get; set; }
        public ApplicationVersion Version { get; set; }
    }
}

namespace GameInterface.Services.Modules.Validators
{
    public sealed class ModuleValidator
    {
        public bool Validate(
            IEnumerable<GameInterface.Services.Modules.ModuleInfo> serverModules,
            IEnumerable<GameInterface.Services.Modules.ModuleInfo> clientModules,
            out string error)
        {
            if (!ValidateGameVersion(serverModules, clientModules, out error))
                return false;

            var serverById = serverModules
                .Where(module => !module.IsOfficial)
                .ToDictionary(module => module.Id);
            foreach (var clientModule in clientModules.Where(module => !module.IsOfficial))
            {
                GameInterface.Services.Modules.ModuleInfo serverModule;
                if (!serverById.TryGetValue(clientModule.Id, out serverModule))
                {
                    error = "Server does not support module '" + clientModule.Id + "'.";
                    return false;
                }
                if (!serverModule.Version.IsSame(clientModule.Version, true))
                {
                    error = "Wrong version of module '" + clientModule.Id + "'.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private static bool ValidateGameVersion(
            IEnumerable<GameInterface.Services.Modules.ModuleInfo> serverModules,
            IEnumerable<GameInterface.Services.Modules.ModuleInfo> clientModules,
            out string error)
        {
            var server = serverModules.First(module => module.IsOfficial);
            var client = clientModules.First(module => module.IsOfficial);
            if (!server.Version.IsSame(client.Version, false))
            {
                error = "Wrong game version detected. Server uses '" +
                        server.Version + "', client uses '" + client.Version + "'.";
                return false;
            }

            error = null;
            return true;
        }
    }
}
