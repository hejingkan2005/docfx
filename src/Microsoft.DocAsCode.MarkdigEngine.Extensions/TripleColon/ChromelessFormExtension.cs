// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.Net;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Microsoft.DocAsCode.MarkdigEngine.Extensions
{
    public class ChromelessFormExtension : ITripleColonExtensionInfo
    {
        public string Name => "form";

        public bool SelfClosing => true;

        public bool TryProcessAttributes(IDictionary<string, string> attributes, out HtmlAttributes htmlAttributes, out IDictionary<string, string> renderProperties, Action<string> logError, Action<string> logWarning, MarkdownObject markdownObject)
        {
            htmlAttributes = null;
            renderProperties = new Dictionary<string, string>();
            var model = "";
            var action = "";
            var submitText = "";
            foreach (var attribute in attributes)
            {
                var name = attribute.Key;
                var value = attribute.Value;
                switch (name)
                {
                    case "model":
                        model = value;
                        break;
                    case "action":
                        action = value;
                        break;
                    case "submittext":
                        submitText = WebUtility.HtmlEncode(value);
                        break;
                    default:
                        logError($"Unexpected attribute \"{name}\".");
                        return false;
                }
            }

            if (string.IsNullOrEmpty(action))
            {
                logError("Form action must be specified.");
                return false;
            }
            if (string.IsNullOrEmpty(submitText))
            {
                logError("Submit text must be specified.");
                return false;
            }

            htmlAttributes = new HtmlAttributes();
            if (!string.IsNullOrEmpty(model))
            {
                htmlAttributes.AddProperty("data-model", model);
            }
            htmlAttributes.AddProperty("data-action", action);
            htmlAttributes.AddClass("chromeless-form");

            renderProperties.Add(new KeyValuePair<string, string>("submitText", submitText));

            return true;
        }

        public bool Render(HtmlRenderer renderer, MarkdownObject markdownObject, Action<string> logWarning)
        {
            var block = (TripleColonBlock)markdownObject;
            block.RenderProperties.TryGetValue("submitText", out var buttonText);

            renderer.Write("<form").WriteAttributes(block).WriteLine(">");
            renderer.WriteLine("<div></div>");
            renderer.WriteLine($"<button class=\"button is-primary\" disabled=\"disabled\" type=\"submit\">{buttonText}</button>");
            renderer.WriteLine("</form>");

            return true;
        }

        public bool TryValidateAncestry(ContainerBlock container, Action<string> logError)
        {
            return true;
        }
    }
}
