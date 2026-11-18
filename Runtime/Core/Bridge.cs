using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FlightmodeSdk
{
    // Builds the window.flightmode bridge injected into a creative's entry HTML.
    // The template matches packages/protocol/src/bridge.js byte for byte.
    public static class Bridge
    {
        public const string Template = "<script>(function(c,post){var fm={locale:c.locale,platform:c.platform,format:c.format,strings:Object.freeze(c.strings),durationMs:c.durationMs,links:Object.freeze(c.links),ready:function(){post({t:\"ready\"})},open:function(id){post({t:\"open\",id:String(id)})},done:function(){post({t:\"done\"})},onStart:null,onPause:null};window.flightmode=fm;window.__flightmodeEmit=function(name,arg){var f=fm[name];if(typeof f===\"function\")f(arg)};})(__CONFIG__,__POST__);</script>";

        public static string Script(CreativeVariant variant, string format, int durationMs, string postSource)
        {
            var config = new Dictionary<string, object>
            {
                ["locale"] = variant.Locale,
                ["platform"] = variant.Platform,
                ["format"] = format,
                ["strings"] = variant.Strings,
                ["durationMs"] = durationMs,
                ["links"] = new List<string>(variant.Links.Keys)
            };
            var json = Json.Stringify(config).Replace("<", "\\u003c");
            return Template.Replace("__CONFIG__", json).Replace("__POST__", postSource);
        }

        // Inserts the bridge right after <head>, like injectBridge in packages/protocol/src/bridge.js.
        public static string Inject(string html, string script)
        {
            var head = Regex.Match(html, "<head[^>]*>", RegexOptions.IgnoreCase);
            if (!head.Success) return script + html;
            var end = head.Index + head.Length;
            return html.Substring(0, end) + script + html.Substring(end);
        }
    }
}
