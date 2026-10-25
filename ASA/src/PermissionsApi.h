#pragma once

// Bridge to the ASA Permissions plugin (Permissions.dll).
//
// The functions are looked up in the loaded Permissions.dll at runtime (GetProcAddress) instead of being
// linked through an import library, so no Permissions.lib is needed to build and none is shipped. The
// names are the decorated (mangled) export names of those functions in Permissions.dll:
//   ?GetPlayerGroups@Permissions@@YA?AV?$TArray@VFString@@V?$TSizedDefaultAllocator@$0CA@@@@@AEBVFString@@@Z
//   ?SubscribePermissionGroupUpdatedCallback@Permissions@@YAXVFString@@AEBV?$function@$$A6AXAEBVFString@@H@Z@std@@@Z
//   ?UnSubscribePermissionGroupUpdatedCallback@Permissions@@YAXVFString@@@Z
// To check them against an installed Permissions.dll:  dumpbin /exports Permissions.dll | findstr Permissions@@

#include <functional>

#include "API/ARK/Ark.h"

namespace PermissionsApi
{
    // The player's permanent groups, their tribe's groups and any timed groups that are active right
    // now (Permissions itself drops timed groups that are still delayed or already expired).
    using GetPlayerGroupsFn = TArray<FString> (*)(const FString& eos_id);

    // Called by Permissions when a player's (eos_id) or a tribe's (tribe_id) groups change, e.g. on
    // permissions.addtimed. A timed group simply expiring is not a change it reports, so the periodic
    // recheck still covers expiry.
    using GroupUpdatedCallback = std::function<void(const FString& eos_id, int tribe_id)>;
    using SubscribeFn = void (*)(FString callback_name, const GroupUpdatedCallback& callback);
    using UnsubscribeFn = void (*)(FString callback_name);

    constexpr const char* kGetPlayerGroups =
        "?GetPlayerGroups@Permissions@@YA?AV?$TArray@VFString@@V?$TSizedDefaultAllocator@$0CA@@@@@AEBVFString@@@Z";
    constexpr const char* kSubscribe =
        "?SubscribePermissionGroupUpdatedCallback@Permissions@@YAXVFString@@AEBV?$function@$$A6AXAEBVFString@@H@Z@std@@@Z";
    constexpr const char* kUnsubscribe =
        "?UnSubscribePermissionGroupUpdatedCallback@Permissions@@YAXVFString@@@Z";

    struct Functions
    {
        GetPlayerGroupsFn get_player_groups = nullptr;   // required
        SubscribeFn subscribe = nullptr;                 // optional: without it, changes are seen on the recheck
        UnsubscribeFn unsubscribe = nullptr;
    };

    enum class Status { NotLoaded, MissingExport, Ready };

    struct Result
    {
        Status status = Status::NotLoaded;
        const Functions* functions = nullptr;
    };

    // Looks the functions up once Permissions.dll is in the process. Until then it reports NotLoaded and
    // tries again on the next call, because ArkApi may load this plugin before Permissions.
    inline Result Resolve()
    {
        static Functions functions;
        static bool resolved = false;
        if (resolved)
            return { functions.get_player_groups != nullptr ? Status::Ready : Status::MissingExport, &functions };

        const HMODULE module = GetModuleHandleW(L"Permissions.dll");
        if (module == nullptr)
            return {};

        functions.get_player_groups = reinterpret_cast<GetPlayerGroupsFn>(reinterpret_cast<void*>(GetProcAddress(module, kGetPlayerGroups)));
        functions.subscribe = reinterpret_cast<SubscribeFn>(reinterpret_cast<void*>(GetProcAddress(module, kSubscribe)));
        functions.unsubscribe = reinterpret_cast<UnsubscribeFn>(reinterpret_cast<void*>(GetProcAddress(module, kUnsubscribe)));
        resolved = true;
        return { functions.get_player_groups != nullptr ? Status::Ready : Status::MissingExport, &functions };
    }
}
