(function () {
    window.smartReview = window.smartReview || {};

    function openHtmlInNewTab(htmlContent, title) {
        var popup = window.open("", "_blank");
        if (!popup) {
            throw new Error("Popup blocked by browser");
        }

        popup.document.open();
        popup.document.write(htmlContent || "<html><body><p>No content.</p></body></html>");
        popup.document.close();
        if (title) {
            popup.document.title = title;
        }
    }

    window.smartReview.openHtmlInNewTab = openHtmlInNewTab;
    window.openHtmlInNewTab = openHtmlInNewTab;
})();
