(function () {
    window.smartReview = window.smartReview || {};
    var html2CanvasLoaderPromise = null;

    function ensureHtml2Canvas() {
        if (window.html2canvas) {
            return Promise.resolve(window.html2canvas);
        }

        if (html2CanvasLoaderPromise) {
            return html2CanvasLoaderPromise;
        }

        html2CanvasLoaderPromise = new Promise(function (resolve, reject) {
            var script = document.createElement("script");
            script.src = "https://cdn.jsdelivr.net/npm/html2canvas@1.4.1/dist/html2canvas.min.js";
            script.async = true;
            script.onload = function () {
                if (window.html2canvas) resolve(window.html2canvas);
                else reject(new Error("html2canvas loaded but unavailable"));
            };
            script.onerror = function () {
                reject(new Error("Failed to load html2canvas"));
            };
            document.head.appendChild(script);
        });

        return html2CanvasLoaderPromise;
    }

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
    window.smartReview.copyTextToClipboard = async function (text) {
        var value = text || "";
        if (navigator.clipboard && navigator.clipboard.writeText) {
            await navigator.clipboard.writeText(value);
            return;
        }

        var ta = document.createElement("textarea");
        ta.value = value;
        ta.setAttribute("readonly", "");
        ta.style.position = "fixed";
        ta.style.opacity = "0";
        document.body.appendChild(ta);
        ta.select();
        document.execCommand("copy");
        document.body.removeChild(ta);
    };

    window.smartReview.scrollToElement = function (elementId) {
        var el = document.getElementById(elementId);
        if (!el) {
            return;
        }

        el.scrollIntoView({ behavior: "smooth", block: "start" });
    };

    window.smartReview.captureElementAsPng = async function (elementId, fileName) {
        var el = document.getElementById(elementId);
        if (!el) {
            throw new Error("Target element not found for capture.");
        }

        var html2canvas = await ensureHtml2Canvas();
        var canvas = await html2canvas(el, {
            scale: Math.max(2, window.devicePixelRatio || 1),
            backgroundColor: "#ffffff",
            useCORS: true,
            logging: false
        });

        var url = canvas.toDataURL("image/png");
        var a = document.createElement("a");
        a.href = url;
        a.download = fileName || "mock-screen.png";
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    };
})();
