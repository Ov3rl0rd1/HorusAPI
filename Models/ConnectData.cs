namespace HorusAPI.Models;

public record ConnectData(
    int    serverId,
    string host,
    string protocol,
    string obfs_type,
    string obfs_password,
    string hop,
    string template);
