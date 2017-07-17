﻿/*
 * The MIT License (MIT)
 * 
 * Copyright (c) 2013-2017  Denis Kuzmin < entry.reg@gmail.com > :: github.com/3F
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using net.r_eg.MvsSln.Core.SlnHandlers;
using net.r_eg.MvsSln.Extensions;

namespace net.r_eg.MvsSln.Core
{
    /// <summary>
    /// Parser for basic elements from .sln files.
    /// 
    /// Please note: initially it was part of https://github.com/3F/vsSolutionBuildEvent
    /// </summary>
    public class SlnParser: ISlnContainer
    {
        /// <summary>
        /// The name of file if used stream from memory.
        /// </summary>
        public const string MEM_FILE = "$None.sln$";

        /// <summary>
        /// To use specific Encoding by default for some operations with data.
        /// </summary>
        protected Encoding encoding = Encoding.Default;

        /// <summary>
        /// Available solution handlers.
        /// </summary>
        public SynchSubscribers<ISlnHandler> SlnHandlers
        {
            get;
            protected set;
        } = new SynchSubscribers<ISlnHandler>();

        /// <summary>
        /// Dictionary of raw xml projects by Guid.
        /// Will be used if projects cannot be accessed from filesystem.
        /// </summary>
        public IDictionary<string, RawText> RawXmlProjects
        {
            get;
            set;
        }

        /// <summary>
        /// Parse of selected .sln file.
        /// </summary>
        /// <param name="sln">Solution file</param>
        /// <param name="type">Allowed type of operations.</param>
        /// <returns></returns>
        public ISlnResult Parse(string sln, SlnItems type)
        {
            if(String.IsNullOrWhiteSpace(sln)) {
                throw new ArgumentNullException(nameof(sln), MsgResource.ValueNoEmptyOrNull);
            }

            using(var reader = new StreamReader(sln, encoding)) {
                return Parse(reader, type);
            }
        }

        /// <summary>
        /// To parse data from used stream.
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="type">Allowed type of operations.</param>
        /// <returns></returns>
        public ISlnResult Parse(StreamReader reader, SlnItems type)
        {
            if(reader == null) {
                throw new ArgumentNullException(nameof(reader), MsgResource.ValueNoEmptyOrNull);
            }

            string sln = (reader.BaseStream is FileStream) ? ((FileStream)reader.BaseStream).Name : MEM_FILE;

            var data = new SlnResult() {
                SolutionDir = GetDirectoryFromFile(sln),
                ResultType  = type,
            };

            HandlePreProcessing(reader, data);
            {
                string line;
                while((line = reader.ReadLine()) != null) {
                    HandlePositioned(reader, line.Trim(), data);
                }
            }
            HandlePostProcessing(reader, data);

            if(data.SolutionConfigs != null)
            {
                data.DefaultConfig = new ConfigItem(
                    ExtractDefaultConfiguration(data.SolutionConfigs), 
                    ExtractDefaultPlatform(data.SolutionConfigs)
                );
            }

            data.Properties = GlobalProperties(
                sln, 
                data.DefaultConfig?.Configuration, 
                data.DefaultConfig?.Platform
            );

            Aliases(data);

            if((type & SlnItems.Env) == SlnItems.Env)
            {
                data.Env = new IsolatedEnv(data, RawXmlProjects);
                if((type & SlnItems.EnvWithMinimalProjects) == SlnItems.EnvWithMinimalProjects) {
                    data.Env.LoadMinimalProjects();
                }
                if((type & SlnItems.EnvWithProjects) == SlnItems.EnvWithProjects) {
                    data.Env.LoadProjects();
                }
            }
            return data;
        }

        public SlnParser()
        {
            SlnHandlers.register(new LProject());
            SlnHandlers.register(new LProjectConfigurationPlatforms());
            SlnHandlers.register(new LSolutionConfigurationPlatforms());
            SlnHandlers.register(new LProjectDependencies());
        }

        protected virtual void HandlePreProcessing(StreamReader stream, SlnResult data)
        {
            foreach(ISlnHandler h in SlnHandlers) {
                h.PreProcessing(stream, data);
            }
        }

        protected virtual void HandlePositioned(StreamReader reader, string line, SlnResult data)
        {
            foreach(ISlnHandler h in SlnHandlers) {
                h.Positioned(reader, line, data);
            }
        }

        protected virtual void HandlePostProcessing(StreamReader stream, SlnResult data)
        {
            foreach(ISlnHandler h in SlnHandlers) {
                h.PostProcessing(stream, data);
            }
        }

        /// <summary>
        /// TODO: another way to manage aliases for data.
        /// </summary>
        /// <param name="data"></param>
        protected void Aliases(SlnResult data)
        {
            SetProjectConfigurationPlatforms(data);
            SetProjectItemsConfigs(data);
        }

        protected void SetProjectConfigurationPlatforms(SlnResult data)
        {
            if(data.SolutionConfigs == null || data.ProjectConfigs == null) {
                return;
            }

            var ret = new Dictionary<IConfPlatform, IConfPlatformPrj[]>();
            foreach(var sln in data.SolutionConfigs) {
                ret[sln] = data.ProjectConfigs.Where(p => (ConfigItem)p.Sln == (ConfigItem)sln).ToArray();
            }

            data.ProjectConfigurationPlatforms = new RoProperties<IConfPlatform, IConfPlatformPrj[]>(ret);
        }

        protected void SetProjectItemsConfigs(SlnResult data)
        {
            if(data.ProjectConfigurationPlatforms == null) {
                return;
            }

            var ret = new List<ProjectItemCfg>();
            foreach(var slnConf in data.ProjectConfigurationPlatforms)
            {
                foreach(var prjConf in slnConf.Value)
                {
                    var link = new ProjectItemCfg(
                        data.ProjectItems
                            .Where(p => p.pGuid == prjConf.PGuid)
                            .FirstOrDefault(),
                        slnConf.Key,
                        prjConf
                    );
                    ret.Add(link);
                }
            }
            data.ProjectItemsConfigs = ret;
        }

        protected RoProperties GlobalProperties(string sln, string configuration, string platform)
        {
            var ret = new Dictionary<string, string>();

            ret["SolutionDir"]      = GetDirectoryFromFile(sln);
            ret["SolutionExt"]      = Path.GetExtension(sln);
            ret["SolutionFileName"] = Path.GetFileName(sln);
            ret["SolutionName"]     = Path.GetFileNameWithoutExtension(sln);
            ret["SolutionPath"]     = sln;

            ret["Configuration"]    = configuration;
            ret["Platform"]         = platform;

            return new RoProperties(ret);
        }

        protected virtual string ExtractDefaultConfiguration(IEnumerable<IConfPlatform> cfg)
        {
            foreach(IConfPlatform c in cfg) {
                if(c.Configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase)) {
                    return c.Configuration;
                }
            }

            foreach(IConfPlatform c in cfg) {
                return c.Configuration;
            }
            return null;
        }

        protected virtual string ExtractDefaultPlatform(IEnumerable<IConfPlatform> cfg)
        {
            foreach(IConfPlatform c in cfg)
            {
                if(c.Platform.Equals("Mixed Platforms", StringComparison.OrdinalIgnoreCase)) {
                    return c.Platform;
                }

                if(c.Platform.Equals("Any CPU", StringComparison.OrdinalIgnoreCase)) {
                    return c.Platform;
                }
            }

            foreach(IConfPlatform c in cfg) {
                return c.Platform;
            }
            return null;
        }

        protected string GetDirectoryFromFile(string file)
        {
            return Path.GetDirectoryName(file).DirectoryPathFormat();
        }
    }
}