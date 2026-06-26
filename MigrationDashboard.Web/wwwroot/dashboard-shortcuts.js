export function registerEditShortcut(dotNetReference) {
    const handler = async (event) => {
        if (!event.ctrlKey || !event.shiftKey || event.key?.toLowerCase() !== "e") {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        await dotNetReference.invokeMethodAsync("HandleGlobalEditShortcut");
    };

    window.addEventListener("keydown", handler, true);

    return {
        dispose() {
            window.removeEventListener("keydown", handler, true);
        }
    };
}

const editModeStorageKey = "migration-dashboard.edit-mode-active";

export function setEditModeConnectionState(isActive) {
    if (isActive) {
        window.sessionStorage.setItem(editModeStorageKey, "true");
        return;
    }

    window.sessionStorage.removeItem(editModeStorageKey);
}
