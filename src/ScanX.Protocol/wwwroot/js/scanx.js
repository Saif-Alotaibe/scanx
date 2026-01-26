
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

    scanSingle(deviceId,settings) {

        this.connection.invoke("ScanSingle",deviceId,settings).catch(err => console.error(err.toString()));
    }

    scanMultiple(deviceId,settings) {

        this.connection.invoke("ScanMultiple", deviceId, settings).catch(err => console.error(err.toString()));
    }

    scanTest() {
        this.connection.invoke("ScanTest").catch(err => console.error(err.toString()));
    }

    getScanners() {

        var url = apiBaseUrl + "scanner";

        var result;

        $.ajax(url, {

            async: false,

            complete: function (xhr,status) {

                var data = xhr.responseJSON;

                result = data;
            }
        })

        return result;
    }

    // TWAIN Methods - Use these for document scanners like Fujitsu fi-8170

    /**
     * Get all available TWAIN scanners.
     * @returns {Promise} Promise that resolves with list of TWAIN scanners
     */
    getTwainScanners() {
        return this.connection.invoke("GetTwainScanners").catch(err => console.error(err.toString()));
    }

    /**
     * Scan a single page using TWAIN (reliable for ADF scanners).
     * @param {string} deviceName - The TWAIN source name
     * @param {object} settings - Scan settings (color, dpi)
     */
    twainScanSingle(deviceName, settings) {
        this.connection.invoke("TwainScanSingle", deviceName, settings).catch(err => console.error(err.toString()));
    }

    /**
     * Scan all pages from ADF using TWAIN (reliable for document scanners).
     * @param {string} deviceName - The TWAIN source name
     * @param {object} settings - Scan settings (color, dpi)
     */
    twainScanMultiple(deviceName, settings) {
        this.connection.invoke("TwainScanMultiple", deviceName, settings).catch(err => console.error(err.toString()));
    }
}