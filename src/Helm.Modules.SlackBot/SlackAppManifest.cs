namespace Helm.Modules.SlackBot;

/// <summary>
/// The Slack app the user creates in their workspace (api.slack.com/apps → Create New App → From a manifest): user
/// permissions only (it reads as the user, with no bot user of its own), Socket Mode (Helm connects out; nothing listens
/// on the PC), and the events of the conversations the user is in. chat:write is there for reply templates later.
/// </summary>
public static class SlackAppManifest
{
    public const string AppsUrl = "https://api.slack.com/apps";

    public const string Json = """
        {
          "display_information": {
            "name": "Helm Slack Bot",
            "description": "Reads my Slack for Helm on my own PC, so I never miss what needs me."
          },
          "oauth_config": {
            "scopes": {
              "user": [
                "channels:history", "channels:read",
                "groups:history", "groups:read",
                "im:history", "im:read",
                "mpim:history", "mpim:read",
                "users:read", "reactions:read", "search:read",
                "chat:write"
              ]
            }
          },
          "settings": {
            "event_subscriptions": {
              "user_events": ["message.channels", "message.groups", "message.im", "message.mpim", "reaction_added"]
            },
            "interactivity": { "is_enabled": false },
            "org_deploy_enabled": false,
            "socket_mode_enabled": true,
            "token_rotation_enabled": false
          }
        }
        """;
}
