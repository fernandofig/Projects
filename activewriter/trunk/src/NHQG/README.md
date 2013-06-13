This is Ayende's NHibernate Query Generator, all credits to him, blah, blah, blah. It's a deprecated utility and it's been superseded by newer NHibernate features like LinQ and QueryOver. ActiveWriter has support for it however, and the Debugging/Tests projects depend on it to run successfully, so I've updated NHQG's NHibernate assembly references and bumped the target framework to 4.0, in order for it compile and run correctly. In short: this is provided only for (pedantic) completeness sake - it's not really necessary nor useful nowadays, and you'd only actually need it in case you want to build the Debugging Project and run the tests successfully, or (gasp!) you need it for legacy projects.

The source is unmodified, only the build structure was slightly modified by getting rid of the NAnt bits, rebuilding the AssemblyInfo file from scratch, in order to simplify things so it can be compiled directly from Visual Studio.

I haven't included Ayende's sign key because I didn't really felt at ease doing that. You can obtain it from Ayende's svn repository on this URL: https://rhino-tools.svn.sourceforge.net/svnroot/rhino-tools/trunk/ . AFAIK, the sign key is not really necessary in order to build or run the utility, so in case this url is no longer accessible at the time you're reading this, you may just disable the signing on the project Properties, on the "Signing" tab (uncheck "Sign the assembly"), and it should still work.

If you're going to run the Debugging Tests, on the diagram's property list of the following .actiw files:

GeneratedCodeNHQGIntegrationWithARTestModel.actiw
GeneratedCodeNHQGIntegrationWithNHTestModel.actiw

You must modify the "NHQG Executable Path" pointing it to the absolute path location of the NHQG binary (and its dependent assemblies*) on your system: unfortunately, Activewriter doesn't support providing relative paths for this.


* NHQG actually depends only on NHibernate.dll, but the Castle assemblies are also thrown in because ActiveWriter searches for them on the same path where NHQG is (when it's enabled) when it's generating code - modifying the "Assembly Path" property of the diagram's property list doesn't seem to help in this case, seems like it's a bug on ActiveWriter, and since I'm not going to use NHQG on real projects anyway, I'm not going to look into it...

