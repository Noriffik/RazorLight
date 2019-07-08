﻿using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RazorLight.Sandbox
{
	class Program
    {
		public static async Task Main()
		{
			var engine = new RazorLightEngineBuilder()
				.UseMemoryCachingProvider()
				.UseEmbeddedResourcesProject(typeof(Program))
				.Build();

			var result = await engine.CompileRenderAsync("Views.Subfolder.A", null);
			Console.WriteLine(result);

			Console.WriteLine("Finished");
		}

		private static readonly object locker = new object();

        private static int _j;
        public static int j
        {
            get
            {
                lock (locker)
                {
                    return _j;
                }
            }
            set
            {
                lock(locker)
                {
                    _j = value;
                }
            }
        }
    }
}
