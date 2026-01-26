
$(function () {

})

var apiBaseUrl = "http://localhost:61234/api/v1/";

class ScanX {

    constructor() {

        this.connection = new signalR.HubConnectionBuilder()
            .withUrl("http://localhost:61234/scanx")
            .configureLogging(signalR.LogLevel.Information)
            .build();
    }

    connect() {

        this.connection.start().catch(err => console.error(err.toString()));


    }

    /**
     * Scan a single page using TWAIN.
     * @param {string} deviceId - The scanner device ID
     * @param {object} settings - Scan settings (color, dpi)
     */
    scanSingle(deviceId, settings) {

        this.connection.invoke("ScanSingle", deviceId, settings).catch(err => console.error(err.toString()));
    }

    /**
     * Scan all pages from ADF using TWAIN.
     * @param {string} deviceId - The scanner device ID
     * @param {object} settings - Scan settings (color, dpi)
     */
    scanMultiple(deviceId, settings) {

        this.connection.invoke("ScanMultiple", deviceId, settings).catch(err => console.error(err.toString()));
    }

    scanTest() {
        this.connection.invoke("ScanTest").catch(err => console.error(err.toString()));
    }

    /**
     * Get all available TWAIN scanners.
     * @returns {Array} List of available scanners
     */
    getScanners() {

        var url = apiBaseUrl + "scanner";

        var result;

        $.ajax(url, {

            async: false,

            complete: function (xhr, status) {

                var data = xhr.responseJSON;

                result = data;
            }
        })

        return result;
    }
}
