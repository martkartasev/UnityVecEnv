import onnx

def rename_tensor(model, old_name, new_name):
    graph = model.graph

    # 1️⃣ Update node inputs/outputs
    for node in graph.node:
        node.input[:] = [new_name if x == old_name else x for x in node.input]
        node.output[:] = [new_name if x == old_name else x for x in node.output]

    # 2️⃣ Update graph inputs
    for tensor in graph.input:
        if tensor.name == old_name:
            tensor.name = new_name

    # 3️⃣ Update graph outputs
    for tensor in graph.output:
        if tensor.name == old_name:
            tensor.name = new_name

    # 4️⃣ Update initializers (weights)
    for initializer in graph.initializer:
        if initializer.name == old_name:
            initializer.name = new_name

    # 5️⃣ Update value_info (shape/type metadata)
    for value_info in graph.value_info:
        if value_info.name == old_name:
            value_info.name = new_name

    return model

for path in ["Grasp__ppo_grasp__1__1772228670_policy.onnx", "Move__ppo_move__1__1772267192_policy.onnx", "Move__ppo_move__1__1772267250_policy.onnx", "Place__ppo_place__1__1772228659_policy.onnx"]:
    model = onnx.load(path)

    # Rename input
    old_input = model.graph.input[0].name
    model = rename_tensor(model, "obs", "obs_continuous")

    # Rename output
    old_output = model.graph.output[0].name
    model = rename_tensor(model, "action", "action_discrete")

    old_output = model.graph.output[0].name
    model = rename_tensor(model, "action_mean", "action_continuous")

    onnx.checker.check_model(model)
    onnx.save(model, "renamed" + path)

