internal static class ApiClassification
{
    internal static string? Native(string library,string api)
    {
        library=library.ToLowerInvariant();
        if(library is "ws2_32.dll" or "wsock32.dll" or "mswsock.dll")
            return api.Contains("addr",StringComparison.OrdinalIgnoreCase)||api.Contains("host",StringComparison.OrdinalIgnoreCase)?"DNS/address":"Winsock transport";
        if(library is "winhttp.dll" or "wininet.dll" or "urlmon.dll" or "websocket.dll" or "httpapi.dll")return "HTTP/WebSocket";
        if(library=="dnsapi.dll")return "DNS/address";
        if(library is "iphlpapi.dll" or "rasapi32.dll" or "wlanapi.dll" or "sensapi.dll")return "Adapter/connectivity query";
        if(library.StartsWith("eossdk",StringComparison.OrdinalIgnoreCase)||library is "steam_api64.dll" or "steam_api.dll")return "Platform online service";
        return null;
    }
    internal static bool Resolver(string api)=>api is "GetProcAddress" or "LoadLibraryA" or "LoadLibraryW" or "LoadLibraryExA" or "LoadLibraryExW" or "LdrLoadDll" or "LdrGetProcedureAddress";
    internal static string Effect(string qualifiedApi,string category)
    {
        var api=qualifiedApi[(qualifiedApi.LastIndexOf('!')+1)..].Split(' ')[0];
        if(category=="Adapter/connectivity query")return "local-adapter-query";
        if(api is "WSAGetLastError" or "WSASetLastError" or "htons" or "htonl" or "ntohs" or "ntohl"
            or "inet_addr" or "inet_ntoa" or "inet_pton" or "inet_ntop" or "InetPtonA" or "InetPtonW" or "InetNtopA" or "InetNtopW"
            or "freeaddrinfo" or "FreeAddrInfoW" or "gethostname" or "gai_strerrorA" or "gai_strerrorW")return "local-helper";
        if(api is "send" or "sendto" or "recv" or "recvfrom" or "connect" or "accept" or "WSAConnect" or "WSAAccept"
            or "WSASend" or "WSASendTo" or "WSARecv" or "WSARecvFrom" or "WSARecvMsg" or "WSASendMsg")return "transport-io";
        if(category=="DNS/address")return "address-resolution-may-use-network";
        if(category=="Winsock transport")return "socket-setup-status-or-poll";
        return "service-api-runtime-behavior-unresolved";
    }
    internal static string? Managed(string type)
    {
        if(type.StartsWith("System.Net.Http",StringComparison.Ordinal)||type.StartsWith("System.Net.Web",StringComparison.Ordinal))return "HTTP/WebSocket";
        if(type.StartsWith("System.Net.Sockets",StringComparison.Ordinal))return "Winsock transport";
        if(type.StartsWith("System.Net.Dns",StringComparison.Ordinal)||type.StartsWith("System.Net.IP",StringComparison.Ordinal))return "DNS/address";
        if(type.StartsWith("System.Net.NetworkInformation",StringComparison.Ordinal))return "Adapter/connectivity query";
        if(type.StartsWith("System.ServiceModel",StringComparison.Ordinal)||type.StartsWith("System.Web.Services",StringComparison.Ordinal))return "SOAP/service client";
        if(type.StartsWith("System.Net.Security",StringComparison.Ordinal))return "TLS stream";
        return null;
    }
}
