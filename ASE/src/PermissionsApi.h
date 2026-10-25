#pragma once

// Bridge to the ASE Permissions plugin that ships with ArkServerApi (Permissions.dll).
//
// The function is looked up in the loaded Permissions.dll at runtime (GetProcAddress) instead of being
// linked through an import library, so no Permissions.lib is needed to build and none is shipped. The
// name is the decorated (mangled) export name of Permissions::GetPlayerGroups(uint64) in Permissions.dll:
//   ?GetPlayerGroups@Permissions@@YA?AV?$TArray@VFString@@VFDefaultAllocator@@@@_K@Z
// To check it against an installed Permissions.dll:  dumpbin /exports Permissions.dll | findstr GetPlayerGroups
//
// It returns the player's permanent groups, their tribe's groups (plus "TribeSize:N"/"TribeOnline:N"
// for an online player) and any timed groups that are active right now. Unlike the ASA build, the
// ASE Permissions plugin has no group-change notification, so changes are found by polling.

#include "API/ARK/Ark.h"

namespace PermissionsApi
{
    using GetPlayerGroupsFn = TArray<FString> (*)(uint64 steam_id);

    constexpr const char* kGetPlayerGroups = "?GetPlayerGroups@Permissions@@YA?AV?$TArray@VFString@@VFDefaultAllocator@@@@_K@Z";

    enum class Status { NotLoaded, MissingExport, Ready };

    struct Result
    {
        Status status = Status::NotLoaded;
        GetPlayerGroupsFn get_player_groups = nullptr;
    };

    // Looks the function up once Permissions.dll is in the process. Until then it reports NotLoaded and
    // tries again on the next call, because ArkApi may load this plugin before Permissions.
    inline Result Resolve()
    {
        static GetPlayerGroupsFn function = nullptr;
        static bool resolved = false;
        if (!resolved)
        {
            const HMODULE module = GetModuleHandleW(L"Permissions.dll");
            if (module == nullptr)
                return {};
            function = reinterpret_cast<GetPlayerGroupsFn>(reinterpret_cast<void*>(GetProcAddress(module, kGetPlayerGroups)));
            resolved = true;
        }
        return { function != nullptr ? Status::Ready : Status::MissingExport, function };
    }
}
