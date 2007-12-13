﻿using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using Castle.MonoRail.Framework;
using Castle.MonoRail.Views.AspView;
namespace CompiledViews
{
	public class usingviewcomponents_usingcomponentwithdotinaliteralparametervalue : AspViewBase
	{
		protected override string ViewName { get { return "\\UsingViewComponents\\UsingComponentWithDotInALiteralParameterValue.aspx"; } }
		protected override string ViewDirectory { get { return "\\UsingViewComponents"; } }


		public override void Render()
		{
Output(@"some text before viewcomponent
");
InvokeViewComponent("Echo", null, null, "out", "with.dot");
Output(@"
some text after viewcomponent");

		}

	}
}