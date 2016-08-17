﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SymbolHandle = System.IntPtr;
using AtomicSymbolCreator = System.IntPtr;
namespace mxnet.csharp
{

    class Operator
    {
       static readonly  OpMap   op_map_ = new OpMap();
        Dictionary<string, string> params_desc_ = new Dictionary<string, string>();
        bool variable_params_ = false;
        Dictionary<string, string> params_ = new Dictionary<string, string>();
        List<SymbolHandle> input_values = new List<SymbolHandle>();
        List<string> input_keys = new List<string>();
        private AtomicSymbolCreator handle_;

        /// <summary>
        /// Operator constructor
        /// </summary>
        /// <param name="operator_name">type of the operator</param>
        Operator(string operator_name)
        {
            handle_ = op_map_.GetSymbolCreator(operator_name);
        }

        /// <summary>
        /// set config parameters
        /// </summary>
        /// <typeparam name="TT"></typeparam>
        /// <param name="name">name of the config parameter</param>
        /// <param name="value">value of the config parameter</param>
        /// <returns></returns>
        Operator SetParam<TT>(string name, TT value)
        {

            params_[name] = value.ToString();
            return this;
        }

        /// <summary>
        /// add an input symbol
        /// </summary>
        /// <param name="name">name name of the input symbol</param>
        /// <param name="symbol">the input symbol</param>
        /// <returns></returns>
        Operator SetInput(string name, Symbol symbol)
        {
            input_keys.Add(name);
            input_values.Add(symbol.GetHandle());
            return this;
        }
        /*!
        * \brief add an input symbol
        * \param symbol the input symbol
        */
        /// <summary>
        /// add an input symbol
        /// </summary>
        /// <param name="symbol">the input symbol</param>
        void PushInput(Symbol symbol)
        {
            input_values.Add(symbol.GetHandle());
        }


        /// <summary>
        /// create a Symbol from the current operator
        /// </summary>
        /// <param name="name">the name of the operator</param>
        /// <returns>the operator Symbol</returns>
        Symbol CreateSymbol(string name = "")
        {
            string pname = name == "" ? null : name;

            SymbolHandle symbol_handle = IntPtr.Zero;
            List<string> input_keys = new List<string>();
            List<string> param_keys = new List<string>();
            List<string> param_values = new List<string>();

            foreach (var data in params_)
            {
                param_keys.Add(data.Key);
                param_values.Add(data.Value);
            }
            foreach (var data in this.input_keys)
            {
                input_keys.Add(data);
            }

            NativeMethods.MXSymbolCreateAtomicSymbol(handle_, (uint)param_keys.Count, param_keys.ToArray(),
                                       param_values.ToArray(), out symbol_handle);
            NativeMethods.MXSymbolCompose(symbol_handle, pname, (uint)input_values.Count, input_keys.ToArray(),
                            input_values.ToArray());
            return new Symbol(symbol_handle);
        }


    }
}
