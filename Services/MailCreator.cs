﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using MailKit;
using MailKit.Net.Smtp;

using MimeKit;

namespace MailDemon
{
    /// <summary>
    /// Creates mail messages
    /// </summary>
    public interface IMailCreator
    {
        /// <summary>
        /// Create mail
        /// </summary>
        /// <param name="templateName">Full template name</param>
        /// <param name="model">Model</param>
        /// <param name="extraInfo">Extra info (view bag)</param>
        /// <param name="htmlModifier">Allow modifying html before it is made part of the message. Params are body and subject.</param>
        /// <returns>MimeMessage with body and subject populated, you will need to set to and from addresses, etc.</returns>
        Task<MimeMessage> CreateMailAsync(string templateName, object model, ExpandoObject extraInfo, Func<string, string, string> htmlModifier);
    }

    /// <summary>
    /// Mail creator implementation with razor light engine
    /// For the mail subject, use <!-- Subject: ... --> inside the body of the template
    /// </summary>
    public class MailCreator : IMailCreator
    {
        private readonly IViewRenderService templateEngine;

        private async Task<MimeMessage> CreateMailInternalAsync(string templateName, object model, ExpandoObject extraInfo, bool allowDefault, Func<string, string, string> htmlModifier)
        {
            IDictionary<string, object> extraInfoDict = extraInfo as IDictionary<string, object>;
            if (!extraInfoDict.ContainsKey("Layout"))
            {
                extraInfoDict["Layout"] = "/Views/_LayoutMail.cshtml";
            }
            string html = await templateEngine.RenderViewToStringAsync(templateName, model, extraInfo);

            if (html != null)
            {
                // find email subject
                Match match = Regex.Match(html, @"\<!-- ?Subject: (?<subject>.*?) ?--\>", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string subjectText = match.Groups["subject"].Value.Trim();

                    // two or more spaces to one space in subject
                    subjectText = Regex.Replace(subjectText, "[\r\n ]+", " ");

                    html = (htmlModifier == null ? html : htmlModifier.Invoke(html, subjectText));
                    html = PreMailer.Net.PreMailer.MoveCssInline(html, true).Html;

                    BodyBuilder builder = new BodyBuilder
                    {
                        HtmlBody = html
                    };
                    return new MimeMessage
                    {
                        Body = builder.ToMessageBody(),
                        Subject = subjectText
                    };
                }
                else
                {
                    throw new InvalidOperationException(Resources.MissingSubjectInTemplate);
                }
            }
            else if (allowDefault)
            {
                templateName = MailTemplate.GetTemplateName(templateName);
                return await CreateMailInternalAsync(templateName + "Default", model, extraInfo, false, null);
            }

            throw new ArgumentException("No view found for name " + templateName);
        }

        /// <summary>
        /// Consructor
        /// </summary>
        /// <param name="templateEngine">View render service</param>
        public MailCreator(IViewRenderService templateEngine)
        {
            this.templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
        }

        /// <inheritdoc />
        public Task<MimeMessage> CreateMailAsync(string templateName, object model, ExpandoObject extraInfo, Func<string, string, string> htmlModifier)
        {
            return CreateMailInternalAsync(templateName, model, extraInfo ?? new ExpandoObject(), true, htmlModifier);
        }
    }
}
