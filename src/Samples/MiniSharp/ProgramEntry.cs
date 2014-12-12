﻿// Copyright 2012 Fan Shi
// 
// This file is part of the VBF project.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Diagnostics;
using System.Linq;
using VBF.Compilers;
using VBF.MiniSharp.Targets.Cil;

namespace VBF.MiniSharp
{
    class ProgramEntry
    {
        static void Main(string[] args)
        {
            string source = @"
static class 程序入口
{
    //中文注释
    public static void Main(string[] args)
    {
        Fac o;
        o = new Fac();
        System.Console.WriteLine(o.ComputeFac(8));
    }
}

class Fac
{
    public int ComputeFac(int num)
    {
        int num_aux;
        if (num < 1)
            num_aux = 1;
        else
            num_aux = num * (this.ComputeFac(num - 1));

        return num_aux;
    }

    public int Foo()
    {
        return 1;
    }
}

";

            Stopwatch sw = new Stopwatch();
            sw.Start();

            CompilationErrorManager errorManager = new CompilationErrorManager();
            CompilationErrorList errorList = errorManager.CreateErrorList();
            MiniSharpParser p = new MiniSharpParser(errorManager);
            p.Initialize();

            sw.Stop();
            Console.WriteLine("Initialize time: {0} ms", sw.ElapsedMilliseconds);
            sw.Restart();

            var ast = p.Parse(source, errorList);

            sw.Stop();
            Console.WriteLine("Parsing time: {0} ms", sw.ElapsedMilliseconds);
            sw.Restart();

            if (errorList.Count != 0)
            {
                ReportErrors(errorList);
                return;
            }

            TypeDeclResolver resolver1 = new TypeDeclResolver(errorManager);
            resolver1.DefineErrors();
            resolver1.ErrorList = errorList;
            resolver1.Visit(ast);

            MemberDeclResolver resolver2 = new MemberDeclResolver(errorManager, resolver1.Types);
            resolver2.DefineErrors();
            resolver2.ErrorList = errorList;
            resolver2.Visit(ast);

            MethodBodyResolver resolver3 = new MethodBodyResolver(errorManager, resolver1.Types);
            resolver3.DefineErrors();
            resolver3.ErrorList = errorList;
            resolver3.Visit(ast);

            sw.Stop();
            Console.WriteLine("Semantic analysis time: {0} ms", sw.ElapsedMilliseconds);

            if (errorList.Count != 0)
            {
                ReportErrors(errorList);
                return;
            }

            //generate Cil
            var codegenDomain = AppDomain.CurrentDomain;
            var cilTrans = new EmitTranslator(codegenDomain, "test");

            cilTrans.Create(ast, @"test.exe");

            ;
        }

        private static void ReportErrors(CompilationErrorList errorList)
        {
            if (errorList.Count > 0)
            {
                foreach (var error in errorList.OrderBy(e => e.ErrorPosition.StartLocation.CharIndex))
                {
                    Console.WriteLine(error.ToString());
                }
            }
        }
    }
}
